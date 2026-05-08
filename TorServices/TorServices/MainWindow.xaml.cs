using System;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using TorServices.Services;
using TorServices.DTOs;
namespace TorServices
{
    // Status badge background color
    public class StatusBgConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            var status = value as string ?? "";
            return status.ToLower() switch
            {
                "completed" => new SolidColorBrush((Color)ColorConverter.ConvertFromString("#D1E7DD")),
                "downloading" => new SolidColorBrush((Color)ColorConverter.ConvertFromString("#CFE2FF")),
                "stopped" => new SolidColorBrush((Color)ColorConverter.ConvertFromString("#F8D7DA")),
                "error" or "failed" => new SolidColorBrush((Color)ColorConverter.ConvertFromString("#F8D7DA")),
                _ => new SolidColorBrush((Color)ColorConverter.ConvertFromString("#E2E3E5")),
            };
        }
        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotImplementedException();
    }

    // Status badge text color
    public class StatusFgConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            var status = value as string ?? "";
            return status.ToLower() switch
            {
                "completed" => new SolidColorBrush((Color)ColorConverter.ConvertFromString("#0F5132")),
                "downloading" => new SolidColorBrush((Color)ColorConverter.ConvertFromString("#084298")),
                "stopped" => new SolidColorBrush((Color)ColorConverter.ConvertFromString("#842029")),
                "error" or "failed" => new SolidColorBrush((Color)ColorConverter.ConvertFromString("#842029")),
                _ => new SolidColorBrush((Color)ColorConverter.ConvertFromString("#41464B")),
            };
        }
        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotImplementedException();
    }

    public partial class MainWindow : Window
    {
        private readonly TorrentService _torrentService;
        private readonly ObservableCollection<TorrentViewModel> _torrents;
        private readonly DispatcherTimer _timer;
        private bool _showHistory = false;

        public MainWindow(TorrentService torrentService)
        {
            InitializeComponent();
            _torrentService = torrentService;
            _torrents = new ObservableCollection<TorrentViewModel>();
            TorrentListView.ItemsSource = _torrents;
            _timer = new DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(1)
            };
            _timer.Tick += Timer_Tick;
            _timer.Start();
            
            // Initialize concurrency slider
            SliderConcurrency.Value = _torrentService.MaxActiveDownloads;
            TxtMaxConcurrency.Text = _torrentService.MaxActiveDownloads.ToString();

            RefreshTorrents();
        }

        private void Timer_Tick(object? sender, EventArgs e)
        {
            RefreshTorrents();
        }

        private void RefreshTorrents()
        {
            var currentTorrents = _showHistory
                ? _torrentService.GetHistory(null).ToList()
                : _torrentService.GetAllTorrents(null).ToList();

            // Remove deleted ones
            var toRemove = _torrents.Where(t => !currentTorrents.Any(c => c.Id == t.Id)).ToList();
            foreach (var r in toRemove) _torrents.Remove(r);

            // Add or update
            foreach (var status in currentTorrents)
            {
                var existing = _torrents.FirstOrDefault(t => t.Id == status.Id);
                if (existing == null)
                {
                    _torrents.Add(new TorrentViewModel(status));
                }
                else
                {
                    existing.Update(status);
                }
            }
        }

        private void Tab_Checked(object sender, RoutedEventArgs e)
        {
            if (TabHistory != null && TabActive != null)
            {
                _showHistory = TabHistory.IsChecked == true;
                
                if (ColSize != null) ColSize.Width = _showHistory ? 0 : 90;
                if (ColPeers != null) ColPeers.Width = _showHistory ? 0 : 55;

                _torrents.Clear();
                RefreshTorrents();
            }
        }
        private async void BtnAddTorrent_Click(object sender, RoutedEventArgs e)
        {
            var openFileDialog = new OpenFileDialog
            {
                Filter = "Torrent files (*.torrent)|*.torrent|All files (*.*)|*.*"
            };

            if (openFileDialog.ShowDialog() == true)
            {
                var folderDialog = new OpenFolderDialog
                {
                    Title = "Select Download Destination",
                    InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory)
                };

                if (folderDialog.ShowDialog() == true)
                {
                    try
                    {
                        await _torrentService.StartTorrent(openFileDialog.FileName, folderDialog.FolderName, null);
                        RefreshTorrents();
                    }
                    catch (Exception ex)
                    {
                        MessageBox.Show(ex.Message, "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                    }
                }
            }
        }

        private async void BtnAddMagnet_Click(object sender, RoutedEventArgs e)
        {
            var inputDialog = new MagnetInputDialog();
            inputDialog.Owner = this;
            if (inputDialog.ShowDialog() == true)
            {
                try
                {
                    await _torrentService.StartMagnet(inputDialog.MagnetUri, inputDialog.OutputDir, null);
                    RefreshTorrents();
                }
                catch (Exception ex)
                {
                    MessageBox.Show(ex.Message, "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
        }

        private TorrentViewModel? GetSelectedTorrent() => TorrentListView.SelectedItem as TorrentViewModel;

        private void ContextMenu_Opened(object sender, RoutedEventArgs e)
        {
            if (MenuToggle != null)
            {
                if (_showHistory)
                {
                    MenuToggle.Visibility = Visibility.Collapsed;
                }
                else
                {
                    MenuToggle.Visibility = Visibility.Visible;
                    var selected = GetSelectedTorrent();
                    if (selected != null)
                    {
                        bool isStopped = selected.Status == "Stopped" || selected.Status == "Error" || selected.Status.StartsWith("Error:");
                        MenuToggle.Header = isStopped ? "▶  Resume" : "⏸  Pause";
                    }
                }
            }
        }

        private async void MenuToggle_Click(object sender, RoutedEventArgs e)
        {
            if (GetSelectedTorrent() is { } selected)
            {
                bool isStopped = selected.Status == "Stopped" || selected.Status == "Error" || selected.Status.StartsWith("Error:");
                try
                {
                    if (isStopped)
                        await _torrentService.ResumeTorrent(selected.Id);
                    else
                        await _torrentService.StopTorrent(selected.Id);

                    RefreshTorrents();
                }
                catch (Exception ex)
                {
                    AppLogger.LogError("PAUSE/RESUME ERROR", ex, $"Torrent='{selected.Name}'");
                    ShowErrorDialog(isStopped ? "Resume Error" : "Pause Error", ex);
                }
            }
        }

        private void ShowErrorDialog(string title, Exception ex)
        {
            var win = new Window
            {
                Title = title,
                Width = 700,
                Height = 460,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Owner = this,
                Background = System.Windows.Media.Brushes.White,
                FontFamily = new System.Windows.Media.FontFamily("Segoe UI"),
                ResizeMode = ResizeMode.CanResize
            };

            var grid = new System.Windows.Controls.Grid();
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            // Header bar
            var header = new Border
            {
                Background = new System.Windows.Media.SolidColorBrush(
                    (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#DC3545")),
                Padding = new Thickness(20, 14, 20, 14)
            };
            var headerText = new System.Windows.Controls.TextBlock
            {
                Text = $"⚠  {title}",
                Foreground = System.Windows.Media.Brushes.White,
                FontSize = 16,
                FontWeight = FontWeights.SemiBold
            };
            header.Child = headerText;
            System.Windows.Controls.Grid.SetRow(header, 0);
            grid.Children.Add(header);

            // Scrollable error body
            var scroll = new System.Windows.Controls.ScrollViewer
            {
                Margin = new Thickness(20, 16, 20, 8),
                VerticalScrollBarVisibility = System.Windows.Controls.ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = System.Windows.Controls.ScrollBarVisibility.Auto
            };
            var body = new System.Windows.Controls.TextBox
            {
                Text = $"Message:\n{ex.Message}\n\nType: {ex.GetType().FullName}\n\nStack Trace:\n{ex.StackTrace}",
                IsReadOnly = true,
                TextWrapping = TextWrapping.Wrap,
                Background = new System.Windows.Media.SolidColorBrush(
                    (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#FFF5F5")),
                BorderThickness = new Thickness(1),
                BorderBrush = new System.Windows.Media.SolidColorBrush(
                    (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#FFCCCC")),
                Foreground = new System.Windows.Media.SolidColorBrush(
                    (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#842029")),
                FontSize = 12,
                FontFamily = new System.Windows.Media.FontFamily("Consolas"),
                Padding = new Thickness(12),
                AcceptsReturn = true,
                VerticalScrollBarVisibility = System.Windows.Controls.ScrollBarVisibility.Disabled
            };
            scroll.Content = body;
            System.Windows.Controls.Grid.SetRow(scroll, 1);
            grid.Children.Add(scroll);

            // Footer with Copy + Close buttons
            var footer = new Border
            {
                Padding = new Thickness(20, 8, 20, 16),
                BorderBrush = new System.Windows.Media.SolidColorBrush(
                    (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#E5E7EB")),
                BorderThickness = new Thickness(0, 1, 0, 0)
            };
            var footerPanel = new System.Windows.Controls.StackPanel
            {
                Orientation = System.Windows.Controls.Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right
            };
            var copyBtn = new System.Windows.Controls.Button
            {
                Content = "📋  Copy to Clipboard",
                Padding = new Thickness(16, 7, 16, 7),
                Margin = new Thickness(0, 0, 8, 0),
                Cursor = System.Windows.Input.Cursors.Hand,
                Background = new System.Windows.Media.SolidColorBrush(
                    (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#E9ECEF")),
                BorderThickness = new Thickness(0)
            };
            copyBtn.Click += (s, _) => Clipboard.SetText(body.Text);
            var closeBtn = new System.Windows.Controls.Button
            {
                Content = "Close",
                Padding = new Thickness(16, 7, 16, 7),
                Background = new System.Windows.Media.SolidColorBrush(
                    (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#DC3545")),
                Foreground = System.Windows.Media.Brushes.White,
                BorderThickness = new Thickness(0),
                Cursor = System.Windows.Input.Cursors.Hand
            };
            closeBtn.Click += (s, _) => win.Close();
            footerPanel.Children.Add(copyBtn);
            footerPanel.Children.Add(closeBtn);
            footer.Child = footerPanel;
            System.Windows.Controls.Grid.SetRow(footer, 2);
            grid.Children.Add(footer);

            win.Content = grid;
            win.ShowDialog();
        }

        private void MenuOpenFolder_Click(object sender, RoutedEventArgs e)
        {
            if (GetSelectedTorrent() is { } selected)
                OpenTorrentFolder(selected);
        }

        private void BtnRowOpenFolder_Click(object sender, RoutedEventArgs e)
        {
            if (sender is System.Windows.Controls.Button btn && btn.Tag is TorrentViewModel selected)
                OpenTorrentFolder(selected);
        }

        private void OpenTorrentFolder(TorrentViewModel selected)
        {
            if (string.IsNullOrEmpty(selected.OutputDir)) return;

            try
            {
                var targetPath = System.IO.Path.Combine(selected.OutputDir, selected.Name);
                if (System.IO.File.Exists(targetPath))
                    System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{targetPath}\"");
                else if (System.IO.Directory.Exists(targetPath))
                    System.Diagnostics.Process.Start("explorer.exe", $"\"{targetPath}\"");
                else if (System.IO.Directory.Exists(selected.OutputDir))
                    System.Diagnostics.Process.Start("explorer.exe", $"\"{selected.OutputDir}\"");
                else
                    MessageBox.Show("Folder not found.", "Not Found", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Could not open folder: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private async void MenuRemove_Click(object sender, RoutedEventArgs e)
        {
            if (GetSelectedTorrent() is { } selected)
            {
                var result = MessageBox.Show($"Remove '{selected.Name}' from the list?", "Remove", MessageBoxButton.YesNo, MessageBoxImage.Question);
                if (result == MessageBoxResult.Yes)
                {
                    await _torrentService.RemoveTorrent(selected.Id, false);
                    RefreshTorrents();
                }
            }
        }

        private async void MenuRemoveData_Click(object sender, RoutedEventArgs e)
        {
            if (GetSelectedTorrent() is { } selected)
            {
                var result = MessageBox.Show($"Remove '{selected.Name}' and delete all downloaded files? This cannot be undone.", "Delete Data", MessageBoxButton.YesNo, MessageBoxImage.Warning);
                if (result == MessageBoxResult.Yes)
                {
                    await _torrentService.RemoveTorrent(selected.Id, true);
                    RefreshTorrents();
                }
            }
        }

        private void Hyperlink_RequestNavigate(object sender, System.Windows.Navigation.RequestNavigateEventArgs e)
        {
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = e.Uri.AbsoluteUri,
                    UseShellExecute = true
                });
                e.Handled = true;
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Could not open link: {ex.Message}");
            }
        }

        private void SliderConcurrency_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_torrentService != null && TxtMaxConcurrency != null)
            {
                int val = (int)e.NewValue;
                _torrentService.MaxActiveDownloads = val;
                TxtMaxConcurrency.Text = val.ToString();
            }
        }
    }

    public class TorrentViewModel : System.ComponentModel.INotifyPropertyChanged
    {
        public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;

        public string Id { get; private set; }
        public string Name { get; private set; } = string.Empty;
        public double Progress { get; private set; }
        public int ActivePeers { get; private set; }
        public string Status { get; private set; } = string.Empty;
        public string FormattedSize { get; private set; } = string.Empty;
        public string OutputDir { get; private set; } = string.Empty;

        public TorrentViewModel(TorrentStatus status)
        {
            Id = status.Id;
            Update(status);
        }

        public void Update(TorrentStatus status)
        {
            if (Name != status.Name) { Name = status.Name; OnPropertyChanged(nameof(Name)); }
            if (Math.Abs(Progress - status.Progress) > 0.01) { Progress = status.Progress; OnPropertyChanged(nameof(Progress)); }
            if (ActivePeers != status.ActivePeers) { ActivePeers = status.ActivePeers; OnPropertyChanged(nameof(ActivePeers)); }
            if (Status != status.Status) { Status = status.Status; OnPropertyChanged(nameof(Status)); }
            if (OutputDir != status.OutputDir) { OutputDir = status.OutputDir ?? string.Empty; OnPropertyChanged(nameof(OutputDir)); }

            var newFormattedSize = FormatBytes(status.TotalSize);
            if (FormattedSize != newFormattedSize) { FormattedSize = newFormattedSize; OnPropertyChanged(nameof(FormattedSize)); }
        }

        private void OnPropertyChanged(string prop) => PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(prop));
        private static string FormatBytes(long bytes)
        {
            string[] suffix = { "B", "KB", "MB", "GB", "TB" };
            int i;
            double dblSByte = bytes;
            for (i = 0; i < suffix.Length && bytes >= 1024; i++, bytes /= 1024)
            {
                dblSByte = bytes / 1024.0;
            }
            return $"{dblSByte:0.##} {suffix[i]}";
        }
    }
}
