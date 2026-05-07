using System.Windows;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using TorServices.Data;
using TorServices.Services;
using Microsoft.AspNetCore.Hosting;
using System.Threading.Tasks;
using System;
namespace TorServices
{
    public partial class App : Application
    {
        private WebApplication? _app;

        private async void Application_Startup(object sender, StartupEventArgs e)
        {
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
                MessageBox.Show($"Application failed to start: {ex.Message}\n\n{ex.StackTrace}", "Startup Error");
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
