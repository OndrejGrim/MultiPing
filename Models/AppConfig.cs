using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace MultiPing.Models;

/// <summary>Persisted application settings, serialized to JSON in the per-user AppData folder.</summary>
public sealed class AppConfig
{
    /// <summary>
    /// Mode the application starts in when no explicit <c>--mode</c> command-line argument is given.
    /// </summary>
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public AppMode DefaultMode { get; set; } = AppMode.PlotPing;

    /// <summary>Target for PlotPing (traceroute) mode.</summary>
    public string PlotPingTarget { get; set; } = "8.8.8.8";

    /// <summary>Configured destination IPs / hostnames for MultiPing mode.</summary>
    public List<string> MultiPingTargets { get; set; } = new() { "8.8.8.8", "1.1.1.1" };

    /// <summary>Displayed plot window in minutes (default 30).</summary>
    public double SampleWindowMinutes { get; set; } = 30;

    /// <summary>Interval between probe rounds, in milliseconds (minimum 1000ms, default 5000ms).</summary>
    public int PingIntervalMs { get; set; } = 5000;

    /// <summary>Per-probe timeout, in milliseconds.</summary>
    public int PingTimeoutMs { get; set; } = 2000;

    /// <summary>Maximum hops to probe in traceroute mode.</summary>
    public int MaxHops { get; set; } = 30;

    /// <summary>Look ahead limit for traceroute optimization.</summary>
    public int LookAheadLimit { get; set; } = 3;

    /// <summary>
    /// Application-wide default for the per-window "Log to disk" toggle. When true, every newly
    /// opened window starts with logging enabled so all traces are logged unless turned off.
    /// </summary>
    public bool LogByDefault { get; set; }

    /// <summary>Directory where log files are written. Empty means the default AppData logs folder.</summary>
    public string LogDirectory { get; set; } = string.Empty;

    /// <summary>Most-recently-used hosts/IPs entered in either mode's target box, newest first.</summary>
    public List<string> RecentHosts { get; set; } = new();
}
