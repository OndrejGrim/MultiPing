using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MultiPing.Models;
using MultiPing.Services;

namespace MultiPing.ViewModels;

/// <summary>
/// Shared plumbing for both modes: the probe loop, start/stop control, the sample-window / scroll state,
/// logging integration, and settings persistence. Derived classes implement a single probe round.
/// </summary>
public abstract partial class MonitorViewModelBase : ObservableObject
{
    public ConfigService ConfigSvc { get; }
    public PingService Ping { get; }
    public TracerouteService Trace { get; }
    public LogService Log { get; }
    public AppConfig Settings { get; }

    private CancellationTokenSource? _cts;

    /// <summary>Raised on the UI thread after each completed probe round so views can refresh singleton plots.</summary>
    public event Action? RoundCompleted;

    protected MonitorViewModelBase(AppConfig settings, ConfigService configSvc, PingService ping, TracerouteService trace, LogService log)
    {
        Settings = settings;
        ConfigSvc = configSvc;
        Ping = ping;
        Trace = trace;
        Log = log;

        _sampleWindowMinutes = settings.General.SampleWindowMinutes;
        _loggingEnabled = settings.Logging.LogByDefault;
        _logByDefault = settings.Logging.LogByDefault;
        _pingIntervalMs = settings.General.PingIntervalMs;

        SelectedSampleWindow = SampleWindowOption.Presets.FirstOrDefault(option =>
            Math.Abs(option.Minutes - settings.General.SampleWindowMinutes) < 0.0001)
            ?? SampleWindowOption.ForMinutes(settings.General.SampleWindowMinutes);

        foreach (string host in settings.General.RecentHosts)
            RecentHosts.Add(host);

        Rows.CollectionChanged += OnRowsCollectionChanged;
    }

    public abstract AppMode Mode { get; }
    public abstract string WindowTitle { get; }

    private const int MaxRecentHosts = 15;

    /// <summary>Most-recently-used hosts/IPs, newest first, shared by both modes' target boxes.</summary>
    public ObservableCollection<string> RecentHosts { get; } = new();

    /// <summary>Records a host as recently used, moving it to the front and persisting the list.</summary>
    protected void RememberHost(string host)
    {
        host = host.Trim();
        if (host.Length == 0) return;

        for (int i = RecentHosts.Count - 1; i >= 0; i--)
            if (string.Equals(RecentHosts[i], host, StringComparison.OrdinalIgnoreCase))
                RecentHosts.RemoveAt(i);

        RecentHosts.Insert(0, host);
        while (RecentHosts.Count > MaxRecentHosts)
            RecentHosts.RemoveAt(RecentHosts.Count - 1);

        Settings.General.RecentHosts = RecentHosts.ToList();
        ConfigSvc.Save(Settings);
    }

    public ObservableCollection<ProbeRowViewModel> Rows { get; } = new();

    public ObservableCollection<SampleWindowOption> SampleWindowOptions { get; } = new(SampleWindowOption.Presets);

    [ObservableProperty]
    private SampleWindowOption? _selectedSampleWindow;

    partial void OnSampleWindowMinutesChanged(double value)
    {
        if (SelectedSampleWindow is null || Math.Abs(SelectedSampleWindow.Minutes - value) > 0.0001)
            SelectedSampleWindow = GetOrCreateSampleWindowOption(value);

        Settings.General.SampleWindowMinutes = value;
        ConfigSvc.Save(Settings);
    }

    partial void OnSelectedSampleWindowChanged(SampleWindowOption? value)
    {
        if (value is null) return;
        if (Math.Abs(SampleWindowMinutes - value.Minutes) > 0.0001)
            SampleWindowMinutes = value.Minutes;
    }

    private SampleWindowOption GetOrCreateSampleWindowOption(double minutes)
    {
        SampleWindowOption? existing = SampleWindowOptions.FirstOrDefault(option =>
            Math.Abs(option.Minutes - minutes) < 0.0001);
        if (existing is not null)
            return existing;

        existing = SampleWindowOption.ForMinutes(minutes);
        SampleWindowOptions.Add(existing);
        return existing;
    }

    /// <summary>
    /// The subset of <see cref="Rows"/> whose plots are enabled, in row order. The bottom panel binds
    /// to this so it divides its height evenly among only the visible plots.
    /// </summary>
    public ObservableCollection<ProbeRowViewModel> PlottedRows { get; } = new();

    private void OnRowsCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        // Reset (e.g. Rows.Clear()) reports no OldItems/NewItems, so drop any tracked rows that are
        // no longer present to avoid stale PlottedRows entries and duplicate PropertyChanged subscriptions.
        if (e.Action == NotifyCollectionChangedAction.Reset)
        {
            foreach (var r in PlottedRows.Where(r => !Rows.Contains(r)).ToList())
            {
                r.PropertyChanged -= OnRowPropertyChanged;
                PlottedRows.Remove(r);
            }
            return;
        }

        if (e.OldItems is not null)
            foreach (ProbeRowViewModel r in e.OldItems)
            {
                r.PropertyChanged -= OnRowPropertyChanged;
                PlottedRows.Remove(r);
            }

        if (e.NewItems is not null)
            foreach (ProbeRowViewModel r in e.NewItems)
            {
                // Guard against double-subscription/duplicate insertion if the same row instance
                // is re-added (e.g. re-added after a Clear()).
                r.PropertyChanged -= OnRowPropertyChanged;
                r.PropertyChanged += OnRowPropertyChanged;
                if (r.PlotEnabled && !PlottedRows.Contains(r)) InsertPlotted(r);
            }
    }

    private void OnRowPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(ProbeRowViewModel.PlotEnabled) || sender is not ProbeRowViewModel r)
            return;

        if (r.PlotEnabled)
        {
            if (!PlottedRows.Contains(r)) InsertPlotted(r);
        }
        else
        {
            PlottedRows.Remove(r);
        }

        if (Settings.MultiPing.RememberPlotSelection)
            OnRowPlotEnabledChanged(r);
    }

    /// <summary>
    /// Hook invoked after a row's plot toggle changes, so derived classes can persist the selection.
    /// Only called when <see cref="MultiPingConfig.RememberPlotSelection"/> is enabled. MultiPing uses this
    /// to remember which destinations show a chart; PlotPing does not persist plot toggles.
    /// </summary>
    protected virtual void OnRowPlotEnabledChanged(ProbeRowViewModel row) { }

    /// <summary>Inserts a row into <see cref="PlottedRows"/> so it keeps the same order as <see cref="Rows"/>.</summary>
    private void InsertPlotted(ProbeRowViewModel r)
    {
        int idx = 0;
        foreach (var row in Rows)
        {
            if (ReferenceEquals(row, r)) break;
            if (row.PlotEnabled && PlottedRows.Contains(row)) idx++;
        }
        PlottedRows.Insert(Math.Min(idx, PlottedRows.Count), r);
    }

    [ObservableProperty] private bool _isRunning;
    [ObservableProperty] 
    [NotifyPropertyChangedFor(nameof(MaxSliderValue))]
    [NotifyPropertyChangedFor(nameof(ShouldShowSlider))]
    private double _sampleWindowMinutes;

    [ObservableProperty] private double _scrollOffsetMinutes;

    [ObservableProperty] 
    [NotifyPropertyChangedFor(nameof(MaxSliderValue))]
    [NotifyPropertyChangedFor(nameof(ShouldShowSlider))]
    private double _totalMinutes;

    [ObservableProperty] private bool _loggingEnabled;
    [ObservableProperty] private bool _logByDefault;
    [ObservableProperty] private int _pingIntervalMs;

    public double MaxSliderValue => TotalMinutes - SampleWindowMinutes;

    public bool ShouldShowSlider => TotalMinutes > SampleWindowMinutes;

    /// <summary>Ping interval in seconds (for UI display). Syncs with PingIntervalMs.</summary>
    public int PingIntervalSeconds
    {
        get => PingIntervalMs / 1000;
        set
        {
            int ms = value * 1000;
            if (PingIntervalMs != ms) PingIntervalMs = ms;
        }
    }
    [ObservableProperty] private string _statusText = "Idle";
    [ObservableProperty] private ProbeRowViewModel? _selectedRow;

    /// <summary>True while a probe round is actually in flight (as opposed to waiting for the next tick).</summary>
    [ObservableProperty] private bool _isProbing;

    /// <summary>Compact label next to the interval setting: "Ping" while a round is in flight, else a countdown like "3s".</summary>
    [ObservableProperty] private string _probeIndicatorText = "";

    /// <summary>Background for the probe indicator pill: green while probing, grey while counting down.</summary>
    [ObservableProperty] private IBrush _probeIndicatorBrush = IdleProbeBrush;

    private static readonly IBrush ProbingBrush = new SolidColorBrush(Color.Parse("#2ECC71"));
    private static readonly IBrush IdleProbeBrush = new SolidColorBrush(Color.Parse("#B0B0B0"));

    partial void OnIsProbingChanged(bool value)
    {
        ProbeIndicatorBrush = value ? ProbingBrush : IdleProbeBrush;
        if (value) ProbeIndicatorText = "Ping";
    }

    /// <summary>Label for the single run toggle button.</summary>
    public string RunButtonText => IsRunning ? "Stop" : "Start";

    partial void OnIsRunningChanged(bool value) => OnPropertyChanged(nameof(RunButtonText));

    partial void OnLoggingEnabledChanged(bool value)
    {
        // Logging only writes while a trace is running; sync the file state to the toggle.
        if (value && IsRunning) OpenLog();
        else if (!value) Log.Close();
    }

    partial void OnLogByDefaultChanged(bool value)
    {
        // App-wide default is persisted immediately so all windows pick it up.
        Settings.Logging.LogByDefault = value;
        ConfigSvc.Save(Settings);
    }

    partial void OnPingIntervalMsChanged(int value)
    {
        Settings.General.PingIntervalMs = value;
        ConfigSvc.Save(Settings);
    }

    [RelayCommand]
    private void ToggleRun()
    {
        if (IsRunning) Stop();
        else Start();
    }

    [RelayCommand]
    private void StartRun() => Start();

    [RelayCommand]
    private void StopRun() => Stop();

    public void Start()
    {
        if (IsRunning) return;
        OnStarting();
        _cts = new CancellationTokenSource();
        IsRunning = true;
        StatusText = "Running";
        if (LoggingEnabled) OpenLog();
        _ = RunLoopAsync(_cts.Token);
    }

    public void Stop()
    {
        if (!IsRunning) return;
        _cts?.Cancel();
        IsRunning = false;
        StatusText = "Stopped";
        Log.Close();
        IsProbing = false;
        ProbeIndicatorText = "";
        OnStopping();
    }

    /// <summary>Hook for derived classes to (re)build their rows before a run begins.</summary>
    protected virtual void OnStarting() { }

    /// <summary>Hook for derived classes to cancel any background work still in flight after a run stops.</summary>
    protected virtual void OnStopping() { }

    /// <summary>Performs one probe round, updating rows/series. Must run on the UI thread.</summary>
    protected abstract Task RunRoundAsync(CancellationToken ct);

    private const int ProgressTickMs = 200;

    private async Task RunLoopAsync(CancellationToken ct)
    {
        DateTime nextFireTime = DateTime.UtcNow.AddMilliseconds(Settings.General.PingIntervalMs);
        CancellationTokenSource? currentRoundCts = null;

        while (!ct.IsCancellationRequested)
        {
            // Wait until next scheduled fire time, ticking the countdown text for the indicator pill.
            while (!ct.IsCancellationRequested)
            {
                double remainingMs = (nextFireTime - DateTime.UtcNow).TotalMilliseconds;
                if (remainingMs <= 0) break;

                ProbeIndicatorText = Math.Ceiling(remainingMs / 1000.0).ToString(CultureInfo.InvariantCulture) + "s";

                try
                {
                    await Task.Delay((int)Math.Min(remainingMs, ProgressTickMs), ct);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }

            if (ct.IsCancellationRequested) break;

            // Fire time has arrived: cancel any in-flight round and start new one
            currentRoundCts?.Cancel();
            currentRoundCts = CancellationTokenSource.CreateLinkedTokenSource(ct);

            IsProbing = true;
            try
            {
                await RunRoundAsync(currentRoundCts.Token);
            }
            catch (OperationCanceledException)
            {
                // Round was aborted (either by next interval or overall stop); timeouts already marked
            }
            catch (Exception ex)
            {
                StatusText = "Error: " + ex.Message;
            }
            finally
            {
                IsProbing = false;
                currentRoundCts?.Dispose();
                currentRoundCts = null;
            }

            if (Log.IsOpen)
                Log.WriteRound(DateTime.Now, Rows.Select(r => r.ToLogEntry()));

            UpdateTotalMinutes();
            RoundCompleted?.Invoke();

            // Schedule next round at fixed wall-clock interval
            nextFireTime = nextFireTime.AddMilliseconds(Settings.General.PingIntervalMs);
        }
    }

    private void OpenLog()
    {
        string dir = string.IsNullOrWhiteSpace(Settings.Logging.LogDirectory)
            ? ConfigService.DefaultLogDirectory
            : Settings.Logging.LogDirectory;
        Log.Open(Mode, dir, DateTime.Now);
        StatusText = Log.CurrentPath is { } p ? "Logging to " + p : StatusText;
    }

    // --- Scrolling of the time-series plots -------------------------------------------------

    /// <summary>Every row's series shares the same oldest timestamp, so any one row's Extent() gives the total span.</summary>
    private void UpdateTotalMinutes()
    {
        (DateTime Oldest, DateTime Newest)? extent = Rows.Count > 0 ? Rows[0].Series.Extent() : null;
        TotalMinutes = extent is { } e ? Math.Max(0, (DateTime.UtcNow - e.Oldest).TotalMinutes) : 0;
    }

    [RelayCommand]
    private void ScrollLeft() => ScrollOffsetMinutes += SampleWindowMinutes / 4.0;

    [RelayCommand]
    private void ScrollRight() =>
        ScrollOffsetMinutes = Math.Max(0, ScrollOffsetMinutes - SampleWindowMinutes / 4.0);

    [RelayCommand]
    private void ScrollLive() => ScrollOffsetMinutes = 0;

    // --- Menu commands ----------------------------------------------------------------------

    [RelayCommand]
    private void NewPlotPingWindow() => WindowLauncher.LaunchNew(AppMode.PlotPing);

    [RelayCommand]
    private void NewMultiPingWindow() => WindowLauncher.LaunchNew(AppMode.MultiPing);

    [RelayCommand]
    private void OpenLogFolder()
    {
        string dir = string.IsNullOrWhiteSpace(Settings.Logging.LogDirectory)
            ? ConfigService.DefaultLogDirectory
            : Settings.Logging.LogDirectory;
        try
        {
            Directory.CreateDirectory(dir);
            Process.Start(new ProcessStartInfo { FileName = dir, UseShellExecute = true });
        }
        catch { /* ignore */ }
    }

    /// <summary>Copies current UI state into settings and persists them. Called on window close.</summary>
    public virtual void SaveSettings()
    {
        Settings.General.SampleWindowMinutes = SampleWindowMinutes;
        ConfigSvc.Save(Settings);
    }
}
