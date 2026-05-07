using System;
using System.IO;
using System.Windows;
using Microsoft.Win32;

namespace TorServices
{
    public partial class MagnetInputDialog : Window
    {
        public string MagnetUri => TxtMagnetUri.Text.Trim();
        public string OutputDir => TxtOutputDir.Text.Trim();

        public MagnetInputDialog()
        {
            InitializeComponent();
            
            // Set default output directory
            TxtOutputDir.Text = @"C:\";
            
            TxtMagnetUri.Focus();
        }

        private void BtnBrowse_Click(object sender, RoutedEventArgs e)
        {
            var folderDialog = new OpenFolderDialog
            {
                Title = "Select Download Destination"
            };

            if (folderDialog.ShowDialog() == true)
            {
                TxtOutputDir.Text = folderDialog.FolderName;
            }
        }

        private void BtnOk_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrWhiteSpace(TxtMagnetUri.Text))
            {
                MessageBox.Show("Please enter a Magnet URI.", "Input Required", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            DialogResult = true;
            Close();
        }

        private void BtnCancel_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }
    }
}
