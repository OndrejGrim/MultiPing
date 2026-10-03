using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MultiPing.Models;
using MultiPing.Services;

namespace MultiPing.ViewModels;

/// <summary>
/// MultiPing mode: each configured destination is pinged directly (like a hop) every round.
/// The top-right panel shows a live traceroute of the currently selected destination.
/// </summary>
public partial class MultiPingViewModel : MonitorViewModelBase
{
    private const int DirectPingTtl = 128;
    private const int SelectedTraceEveryRounds = 5;

    private int _roundCounter;
    private int _nextIndex = 1;
    private CancellationTokenSource? _traceCts;

    /// <summary>Number of traceroute rounds currently in flight. Non-zero shows the "pending" indicator.</summary>
    private int _inflightTraceCount;

    /// <summary>Rows for the selected-destination traceroute, keyed by hop TTL. These persist across
    /// trace rounds so samples from a slow trace that finishes after a newer one still land in the
    /// correct chronological position (each trace timestamps its samples by its own start time).</summary>
    private readonly Dictionary<int, ProbeRowViewModel> _traceRowsByTtl = new();

    /// <summary>The most recently completed trace's hops, used to refresh display labels/addresses.</summary>
    private IReadOnlyList<HopResult>? _lastAppliedHops;

    /// <summary>The host currently being traced. Changed when the selected destination changes.</summary>
    private string? _tracedHost;

    [ObservableProperty] private string _newTargetInput = string.Empty;

    /// <summary>Hops of the traceroute to the currently selected destination (top-right panel).</summary>
    public ObservableCollection<ProbeRowViewModel> SelectedTraceHops { get; } = new();

    /// <summary>Non-empty while at least one traceroute round is in flight, e.g. "pending…".</summary>
    [ObservableProperty]
    private string _traceStatus = "";

    /// <summary>True while at least one traceroute round is in flight, for the pending indicator.</summary>
    [ObservableProperty]
    private bool _traceInProgress;

    public MultiPingViewModel(AppConfig settings, ConfigService configSvc, PingService ping, TracerouteService trace, LogService log)
        : base(settings, configSvc, ping, trace, log)
    {
        // Restore the saved per-target plot toggles so the charts are visible right after startup.
        // PlotEnabled is set before the row is added so the base class picks it up in one step.
        var plotted = settings.RememberPlotSelection
            ? new HashSet<string>(settings.MultiPingPlotTargets, StringComparer.OrdinalIgnoreCase)
            : new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (string host in settings.MultiPingTargets)
        {
            ProbeRowViewModel row = CreateRow(host);
            row.PlotEnabled = plotted.Contains(host);
            Rows.Add(row);
        }
    }

    public override AppMode Mode => AppMode.MultiPing;
    public override string WindowTitle => "MultiPing — Multiple destinations";

    private ProbeRowViewModel CreateRow(string host) =>
        new(_nextIndex++, host) { DisplayLabel = host, IpAddress = host };

    [RelayCommand]
    private void AddTarget()
    {
        string host = NewTargetInput.Trim();
        if (host.Length == 0) return;
        Rows.Add(CreateRow(host));
        RememberHost(host);
        NewTargetInput = string.Empty;
        SaveSettings();
    }

    [RelayCommand]
    private void RemoveSelected()
    {
        if (SelectedRow is { } row)
        {
            Rows.Remove(row);
            SaveSettings();
        }
    }

    protected override async Task RunRoundAsync(CancellationToken ct)
    {
        ProbeRowViewModel[] rows = Rows.ToArray();
        if (rows.Length == 0) return;

        var tasks = rows.Select(r => Ping.ProbeAsync(r.Host, DirectPingTtl, Settings.PingTimeoutMs, ct)).ToArray();
        ProbeResult[] results = await Task.WhenAll(tasks);

        for (int i = 0; i < rows.Length; i++)
        {
            ProbeResult r = results[i];
            if (r.Address is not null)
                rows[i].IpAddress = r.Address.ToString();
            rows[i].AddSample(new PingSample(DateTime.UtcNow, r.RttMs));
        }

        // Periodically refresh the traceroute for the selected destination. Fired without awaiting so a slow
        // or unresponsive host being traced doesn't hold up the ping round's timing.
        if (SelectedRow is { } sel && _roundCounter % SelectedTraceEveryRounds == 0)
            StartSelectedTraceUpdate(sel.Host);
        _roundCounter++;
    }

    protected override void OnStopping()
    {
        _traceCts?.Cancel();
        // Any inflight trace will observe cancellation and bail out, releasing its slot in the
        // finally block; clear the indicator synchronously so the UI reflects "stopped" immediately.
        _inflightTraceCount = 0;
        TraceStatus = "";
        TraceInProgress = false;

        // A cancelled trace may have left per-hop slots marked inflight with no newer trace to
        // fill them. Clear the reservation so the cells revert to their last-known RTT.
        foreach (var row in _traceRowsByTtl.Values)
            row.ClearInflight();
    }

    private void StartSelectedTraceUpdate(string host)
    {
        // Cancel any still-running trace from a previous interval. A newer trace that started later
        // is not "better" — it just started later — so we don't discard a slower trace's results;
        // each trace timestamps its samples by its own start time and merges into the persistent
        // per-hop rows, so out-of-order completion lands in the correct chronological position.
        _traceCts?.Cancel();
        _traceCts?.Dispose();
        var cts = new CancellationTokenSource();
        _traceCts = cts;

        // When the traced host changes, the old per-hop rows no longer apply — reset them so the
        // new trace starts from a clean slate (any inflight trace for the old host is cancelled above).
        if (!string.Equals(host, _tracedHost, StringComparison.OrdinalIgnoreCase))
        {
            _tracedHost = host;
            _traceRowsByTtl.Clear();
            _lastAppliedHops = null;
            SelectedTraceHops.Clear();
        }

        DateTime startedAt = DateTime.UtcNow;

        // Reserve each hop's slot up front so the result set shows a "pending" entry for every TTL
        // the trace is about to probe. The plot ignores inflight samples; the cell shows amber until
        // the result lands. This reserves the position even before the trace completes.
        ReserveInflightSlots();

        Interlocked.Increment(ref _inflightTraceCount);
        TraceStatus = "pending…";
        TraceInProgress = true;
        _ = RunSelectedTraceAsync(host, startedAt, cts.Token);
    }

    private async Task RunSelectedTraceAsync(string host, DateTime startedAt, CancellationToken ct)
    {
        try
        {
            IReadOnlyList<HopResult>? hops = null;
            try
            {
                hops = await Trace.RunRoundAsync(host, Settings.MaxHops, Settings.PingTimeoutMs, Settings.LookAheadLimit, ct);
            }
            catch (OperationCanceledException)
            {
                // Superseded by a newer trace or the run was stopped. The slots this trace reserved
                // stay inflight until a newer trace either fills them or re-reserves them, so a
                // superseded trace's "pending" cells are naturally replaced by the next trace's.
                return;
            }
            catch (Exception ex)
            {
                StatusText = "Trace error: " + ex.Message;
                return;
            }

            await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() => ApplyTraceResults(hops, startedAt));
        }
        finally
        {
            // Always release the inflight slot, even on cancellation, so the pending indicator
            // clears and the next scheduled trace can start.
            if (Interlocked.Decrement(ref _inflightTraceCount) == 0)
            {
                TraceStatus = "";
                TraceInProgress = false;
            }
        }
    }

    /// <summary>Merges one trace round's samples into the persistent per-hop rows. Each hop's sample
    /// is timestamped by this trace's <paramref name="startedAt"/>, so a slow trace that finishes
    /// after a newer one still places its sample in the correct chronological slot.</summary>
    private void ApplyTraceResults(IReadOnlyList<HopResult>? hops, DateTime startedAt)
    {
        if (hops is null) return;

        // Refresh display labels/addresses from the most recently completed trace, but keep every
        // row that has ever been seen (so a slow trace's samples aren't lost when a newer trace
        // trims the display set).
        _lastAppliedHops = hops;
        var displayHops = Services.TracerouteService.TrimForDisplay(hops);

        foreach (var hop in displayHops)
        {
            ProbeRowViewModel row = GetOrCreateTraceRow(hop.Ttl);
            if (hop.Address is not null)
            {
                string ip = hop.Address.ToString();
                row.IpAddress = ip;
                row.DisplayLabel = $"{hop.Ttl}. {ip}";
            }
            else if (row.IpAddress == "*" || string.IsNullOrEmpty(row.IpAddress) || row.IpAddress == row.Host)
            {
                row.IpAddress = "*";
                row.DisplayLabel = $"{hop.Ttl}. *";
            }
            row.AddSample(new PingSample(startedAt, hop.RttMs));
            // The result has landed: clear the inflight reservation for this hop so the cell
            // reflects the actual RTT instead of the pending style.
            row.ClearInflight();
        }

        // Rebuild the displayed collection from the persistent rows, in TTL order, so the panel
        // always reflects the latest known state of every hop that has been probed.
        RefreshTraceDisplay();
    }

    private ProbeRowViewModel GetOrCreateTraceRow(int ttl)
    {
        if (!_traceRowsByTtl.TryGetValue(ttl, out var row))
        {
            row = new ProbeRowViewModel(ttl, host: "*") { DisplayLabel = $"{ttl}. *", IpAddress = "*" };
            _traceRowsByTtl[ttl] = row;
        }
        return row;
    }

    /// <summary>Reserves every known hop's slot as inflight when a trace starts, so the result set
    /// shows a "pending" entry for each position before the trace completes. The plot ignores
    /// inflight samples; only the cell styling reflects the pending state.</summary>
    private void ReserveInflightSlots()
    {
        foreach (var row in _traceRowsByTtl.Values)
            row.MarkInflight();
    }

    private void RefreshTraceDisplay()
    {
        SelectedTraceHops.Clear();
        if (_lastAppliedHops is null) return;

        var displayHops = Services.TracerouteService.TrimForDisplay(_lastAppliedHops);
        foreach (var hop in displayHops)
        {
            if (_traceRowsByTtl.TryGetValue(hop.Ttl, out var row))
                SelectedTraceHops.Add(row);
        }
    }

    /// <summary>Persist the plot toggle immediately so a crash or forced close doesn't lose it.</summary>
    protected override void OnRowPlotEnabledChanged(ProbeRowViewModel row) => SaveSettings();

    public override void SaveSettings()
    {
        Settings.MultiPingTargets = Rows.Select(r => r.Host).ToList();
        if (Settings.RememberPlotSelection)
            Settings.MultiPingPlotTargets = Rows.Where(r => r.PlotEnabled).Select(r => r.Host).ToList();
        base.SaveSettings();
    }
}
