using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using MultiPing.Models;
using MultiPing.Services;
using MultiPing.ViewModels;
using MultiPing.Views;

namespace MultiPing;

public partial class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var configSvc = new ConfigService();
            AppConfig settings = configSvc.Load();

            var ping = new PingService();
            var trace = new TracerouteService(ping);
            var log = new LogService();

            // Explicit --mode wins; otherwise fall back to the user's configured default mode.
            AppMode mode = Program.StartupMode ?? settings.DefaultMode;

            MonitorViewModelBase vm = mode == AppMode.MultiPing
                ? new MultiPingViewModel(settings, configSvc, ping, trace, log)
                : new PlotPingViewModel(settings, configSvc, ping, trace, log);

            desktop.MainWindow = new MainWindow { DataContext = vm };
        }

        base.OnFrameworkInitializationCompleted();
    }
}
