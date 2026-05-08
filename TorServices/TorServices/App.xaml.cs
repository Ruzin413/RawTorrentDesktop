using System.Windows;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using TorServices.Data;
using TorServices.Services;
using Microsoft.AspNetCore.Hosting;
using System.Threading.Tasks;
using System;
using System.Diagnostics;
using System.IO;
namespace TorServices
{
    // ── Simple file logger ─────────────────────────────────────────────────────
    public static class AppLogger
    {
        private static readonly string LogPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
            "rawtorrent_errors.log");

        // Read from appsettings.json: "FileLogging": { "Enabled": true/false }
        private static readonly bool _enabled = ReadEnabledFromSettings();

        private static bool ReadEnabledFromSettings()
        {
            try
            {
                var path = Path.Combine(AppContext.BaseDirectory, "appsettings.json");
                if (!File.Exists(path)) return false;
                var json = File.ReadAllText(path);
                // Simple parse — look for "Enabled": true/false
                var match = System.Text.RegularExpressions.Regex.Match(
                    json, @"""FileLogging""\s*:\s*\{\s*""Enabled""\s*:\s*(true|false)", 
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                return match.Success && match.Groups[1].Value.Equals("true", StringComparison.OrdinalIgnoreCase);
            }
            catch { return false; }
        }

        public static void LogError(string tag, Exception? ex, string? extra = null)
        {
            try
            {
                var line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] [{tag}]";
                if (extra != null) line += $" {extra}";
                if (ex != null)
                {
                    line += $"\n  Type   : {ex.GetType().FullName}";
                    line += $"\n  Message: {ex.Message}";
                    line += $"\n  Stack  :\n{ex.StackTrace}";
                    if (ex.InnerException != null)
                        line += $"\n  Inner  : {ex.InnerException}";
                }
                line += "\n" + new string('-', 80);
                if (_enabled) File.AppendAllText(LogPath, line + "\n");
                Console.WriteLine(line);
                Debug.WriteLine(line);
            }
            catch { /* never crash inside the logger */ }
        }

        public static void Log(string tag, string message)
        {
            try
            {
                var line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] [{tag}] {message}";
                if (_enabled) File.AppendAllText(LogPath, line + "\n");
                Console.WriteLine(line);
            }
            catch { }
        }
    }
    // ──────────────────────────────────────────────────────────────────────────

    public partial class App : Application
    {
        private WebApplication? _app;
        private async void Application_Startup(object sender, StartupEventArgs e)
        {
            // ── Global exception hooks ──────────────────────────────────────────
            // 1. Unhandled exceptions on the WPF UI (Dispatcher) thread
            DispatcherUnhandledException += (s, ex) =>
            {
                AppLogger.LogError("UI ERROR", ex.Exception);
                MessageBox.Show(
                    $"Message:\n{ex.Exception.Message}\n\nType: {ex.Exception.GetType().FullName}\n\nStack Trace:\n{ex.Exception.StackTrace}\n\nFull details saved to:\n{Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory)}\\rawtorrent_errors.log",
                    "Unhandled UI Error",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
                ex.Handled = true;
            };

            // 2. Unhandled exceptions on any background / ThreadPool thread
            AppDomain.CurrentDomain.UnhandledException += (s, ex) =>
            {
                var err = ex.ExceptionObject as Exception;
                AppLogger.LogError("FATAL ERROR", err, $"IsTerminating={ex.IsTerminating}");
            };

            // 3. Exceptions in Tasks that were never awaited / observed
            TaskScheduler.UnobservedTaskException += (s, ex) =>
            {
                AppLogger.LogError("TASK ERROR", ex.Exception);
                ex.SetObserved();
            };
            // ───────────────────────────────────────────────────────────────────

            AppLogger.Log("STARTUP", "Application starting...");

            try
            {
                var builder = WebApplication.CreateBuilder(e.Args);

                // Add services to the container.
                builder.Services.AddControllers();
                builder.Services.AddEndpointsApiExplorer();
                builder.Services.AddSwaggerGen();

                // CSV-based local storage (no database needed)
                builder.Services.AddSingleton<CsvDataStore>();

                // Register TorrentService as a singleton
                builder.Services.AddSingleton<TorrentService>();

                builder.Services.AddCors(options =>
                {
                    options.AddPolicy("AllowAll",
                        b =>
                        {
                            b.AllowAnyOrigin()
                             .AllowAnyMethod()
                             .AllowAnyHeader();
                        });
                });

                // Register MainWindow in DI so it can use services
                builder.Services.AddSingleton<MainWindow>();
                // Port Selection Logic
                int port = 5000;
                bool started = false;
                while (!started && port < 5100)
                {
                    try
                    {
                        builder.WebHost.UseUrls($"http://localhost:{port}");
                        _app = builder.Build();
                        
                        _app.UseCors("AllowAll");
                        if (_app.Environment.IsDevelopment())
                        {
                            _app.UseSwagger();
                            _app.UseSwaggerUI();
                        }
                        _app.UseAuthorization();
                        _app.MapControllers();

                        await Task.Run(async () => await _app.StartAsync());
                        started = true;
                    }
                    catch (System.IO.IOException)
                    {
                        port++;
                        // Re-create builder because it might be in a bad state after build failure
                        builder = WebApplication.CreateBuilder(e.Args);
                        builder.Services.AddControllers();
                        builder.Services.AddEndpointsApiExplorer();
                        builder.Services.AddSwaggerGen();
                        builder.Services.AddSingleton<CsvDataStore>();
                        builder.Services.AddSingleton<TorrentService>();
                        builder.Services.AddCors(options => { options.AddPolicy("AllowAll", b => { b.AllowAnyOrigin().AllowAnyMethod().AllowAnyHeader(); }); });
                        builder.Services.AddSingleton<MainWindow>();
                    }
                }

                if (!started) throw new Exception("Could not find an available port for the server.");

                var mainWindow = _app!.Services.GetRequiredService<MainWindow>();
                mainWindow.Show();
            }
            catch (System.Exception ex)
            {
                AppLogger.LogError("STARTUP ERROR", ex);
                MessageBox.Show($"Application failed to start: {ex.Message}\n\n{ex.StackTrace}\n\nSee Desktop\\rawtorrent_errors.log for details.", "Startup Error");
                Application.Current.Shutdown();
            }
        }
        protected override async void OnExit(ExitEventArgs e)
        {
            if (_app != null)
            {
                await _app.StopAsync();
                await _app.DisposeAsync();
            }
            base.OnExit(e);
        }
    }
}
