using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace MultiPing.Models;

/// <summary>
/// Persisted application settings, serialized to JSON in the per-user AppData folder.
/// The layout mirrors the tabs of the Options dialog: General, MultiPing, Traceroute and Logging.
/// </summary>
public sealed class AppConfig
{
    public GeneralConfig General { get; set; } = new();

    public MultiPingConfig MultiPing { get; set; } = new();

    public TracerouteConfig Traceroute { get; set; } = new();

    public LoggingConfig Logging { get; set; } = new();

    /// <summary>
    /// Replaces any section or list that came back <c>null</c> from deserialization (missing or
    /// explicitly null in the JSON) with its default so callers never have to null-check.
    /// </summary>
    public void Normalize()
    {
        General ??= new GeneralConfig();
        MultiPing ??= new MultiPingConfig();
        Traceroute ??= new TracerouteConfig();
        Logging ??= new LoggingConfig();

        General.RecentHosts ??= new List<string>();
        MultiPing.Targets ??= new List<string>();
        MultiPing.PlotTargets ??= new List<string>();
        Traceroute.PlotPingTarget ??= string.Empty;
        Logging.LogDirectory ??= string.Empty;
    }
}

/// <summary>Settings from the "General" tab plus state shared by both modes.</summary>
public sealed class GeneralConfig
{
    /// <summary>
    /// Mode the application starts in when no explicit <c>--mode</c> command-line argument is given.
    /// </summary>
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public AppMode DefaultMode { get; set; } = AppMode.PlotPing;

    /// <summary>Interval between probe rounds, in milliseconds (minimum 1000ms, default 5000ms).</summary>
    public int PingIntervalMs { get; set; } = 5000;

    /// <summary>Displayed plot window in minutes (default 30).</summary>
    public double SampleWindowMinutes { get; set; } = 30;

    /// <summary>Per-probe timeout, in milliseconds.</summary>
    public int PingTimeoutMs { get; set; } = 2000;

    /// <summary>Most-recently-used hosts/IPs entered in either mode's target box, newest first.</summary>
    public List<string> RecentHosts { get; set; } = new();
}

/// <summary>Settings from the "MultiPing" tab plus the MultiPing window state.</summary>
public sealed class MultiPingConfig
{
    /// <summary>
    /// When true, MultiPing remembers which destinations have their plot enabled and restores that
    /// selection on startup so the time-series charts appear immediately.
    /// </summary>
    public bool RememberPlotSelection { get; set; } = true;

    /// <summary>
    /// When true, a MultiPing window begins probing its configured destinations immediately after
    /// the application starts, without waiting for the user to press Start.
    /// </summary>
    public bool AutoStart { get; set; }

    /// <summary>Configured destination IPs / hostnames for MultiPing mode.</summary>
    public List<string> Targets { get; set; } = new() { "8.8.8.8", "1.1.1.1" };

    /// <summary>MultiPing destinations whose time-series plot was enabled when last saved.</summary>
    public List<string> PlotTargets { get; set; } = new();
}

/// <summary>Settings from the "Traceroute" tab plus the PlotPing (traceroute) window state.</summary>
public sealed class TracerouteConfig
{
    /// <summary>Maximum hops to probe in traceroute mode.</summary>
    public int MaxHops { get; set; } = 30;

    /// <summary>Look ahead limit for traceroute optimization.</summary>
    public int LookAheadLimit { get; set; } = 3;

    /// <summary>Target for PlotPing (traceroute) mode.</summary>
    public string PlotPingTarget { get; set; } = "8.8.8.8";
}

/// <summary>Settings from the "Logging" tab.</summary>
public sealed class LoggingConfig
{
    /// <summary>
    /// Application-wide default for the per-window "Log to disk" toggle. When true, every newly
    /// opened window starts with logging enabled so all traces are logged unless turned off.
    /// </summary>
    public bool LogByDefault { get; set; }

    /// <summary>Directory where log files are written. Empty means the default AppData logs folder.</summary>
    public string LogDirectory { get; set; } = string.Empty;
}
