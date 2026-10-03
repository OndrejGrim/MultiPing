using System;
using Avalonia;
using MultiPing.Models;

namespace MultiPing;

internal static class Program
{
    /// <summary>
    /// Mode explicitly requested via <c>--mode</c> on the command line, parsed before Avalonia starts.
    /// <c>null</c> when no mode was supplied; the configured default mode is used in that case.
    /// </summary>
    public static AppMode? StartupMode { get; private set; }

    // Initialization code. Don't use any Avalonia, third-party APIs or any
    // SynchronizationContext-reliant code before AppMain is called.
    [STAThread]
    public static void Main(string[] args)
    {
        StartupMode = ParseMode(args);
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    private static AppMode? ParseMode(string[] args)
    {
        for (int i = 0; i < args.Length - 1; i++)
        {
            if (!string.Equals(args[i], "--mode", StringComparison.OrdinalIgnoreCase)) continue;
            return args[i + 1].ToLowerInvariant() switch
            {
                "multiping" => AppMode.MultiPing,
                "plotping" => AppMode.PlotPing,
                _ => null,
            };
        }
        return null;
    }

    // Avalonia configuration, don't remove; also used by visual designer.
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
