using System;
using System.Collections.Generic;
using System.Globalization;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MultiPing.Models;
using MultiPing.Services;

namespace MultiPing.ViewModels;

/// <summary>
/// A single row in the top-left grid — either a traceroute hop or a MultiPing destination.
/// Wraps a <see cref="SampleSeries"/> and exposes formatted statistics plus a per-row plot toggle.
/// </summary>
public partial class ProbeRowViewModel : ObservableObject
{
    /// <summary>Raised on the UI thread after this row's series receives new data, so plots can redraw.</summary>
    public event Action? SeriesUpdated;

    public ProbeRowViewModel(int index, string host)
    {
        Index = index;
        Host = host;
        _ipAddress = host;
        _displayLabel = host;
    }

    /// <summary>Hop number (traceroute) or destination row number (MultiPing).</summary>
    public int Index { get; }

    /// <summary>The configured target host (MultiPing). For a hop this equals the responding IP.</summary>
    public string Host { get; }

    /// <summary>Rolling sample buffer feeding both the statistics and the time-series plot.</summary>
    public SampleSeries Series { get; } = new();

    [ObservableProperty] private string _ipAddress;
    [ObservableProperty] private string _displayLabel;
    [ObservableProperty] private bool _plotEnabled;

    [ObservableProperty] private string _rttText = "-";
    [ObservableProperty] private string _minText = "-";
    [ObservableProperty] private string _maxText = "-";
    [ObservableProperty] private string _avgText = "-";
    [ObservableProperty] private string _plText = "0.0";

    /// <summary>Background for the hop/# cell, colour-coded by the most recent RTT.</summary>
    [ObservableProperty] private IBrush _rttBackground = Brushes.Transparent;

    /// <summary>True while this hop's probe is currently in flight (a trace round is running but has
    /// not yet reported a result for this TTL). The plot ignores inflight samples; the cell shows
    /// a "pending" style until the result lands.</summary>
    [ObservableProperty] private bool _inflight;

    private static readonly IBrush LowRttBrush = new SolidColorBrush(Color.Parse("#90EE90"));    // light green: < 50ms
    private static readonly IBrush MidRttBrush = new SolidColorBrush(Color.Parse("#FFA500"));    // orange: < 100ms
    private static readonly IBrush HighRttBrush = new SolidColorBrush(Color.Parse("#FFB6C1"));   // pink: >= 100ms


    /// <summary>Toggles whether this row's time-series plot is shown along the bottom.</summary>
    [RelayCommand]
    private void TogglePlot() => PlotEnabled = !PlotEnabled;

    /// <summary>Adds a sample, recomputes the displayed statistics, and notifies subscribers.</summary>
    public void AddSample(PingSample sample)
    {
        Series.Add(sample);
        RefreshStats();
        SeriesUpdated?.Invoke();
    }

    /// <summary>
    /// Merges samples restored from outside the live probe loop (log history) and refreshes the row once.
    /// </summary>
    public void MergeSamples(IReadOnlyList<PingSample> samples)
    {
        if (samples.Count == 0 || Series.Merge(samples) == 0)
            return;
        RefreshStats();
        SeriesUpdated?.Invoke();
    }

    /// <summary>Reserves this hop's slot for an in-flight probe: shows a "pending" style and hides
    /// the last RTT so the plot ignores the inflight sample until it lands.</summary>
    public void MarkInflight()
    {
        Inflight = true;
        RttText = "…";
        RttBackground = PendingBrush;
    }

    /// <summary>Clears the inflight reservation after the probe result has been applied.</summary>
    public void ClearInflight()
    {
        Inflight = false;
        RefreshStats();
    }

    private static readonly IBrush PendingBrush = new SolidColorBrush(Color.Parse("#FFD700")); // amber: pending

    public void RefreshStats()
    {
        SeriesStatistics s = Series.Snapshot();
        RttText = Num(s.Last);
        MinText = Num(s.Min);
        MaxText = Num(s.Max);
        AvgText = Num(s.Avg);
        PlText = s.PacketLossPercent.ToString("0", CultureInfo.InvariantCulture);
        RttBackground = s.Last switch
        {
            null => Brushes.White,
            < 50 => LowRttBrush,
            < 100 => MidRttBrush,
            _ => HighRttBrush,
        };
    }

    public LogEntry ToLogEntry() => new(Index, IpAddress, Series.Snapshot());

    private static string Num(double? v) =>
        v is double d ? d.ToString("0.0", CultureInfo.InvariantCulture) : "-";
}
