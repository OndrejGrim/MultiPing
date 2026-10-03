using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using MultiPing.Models;
using MultiPing.Services;

namespace MultiPing.ViewModels;

/// <summary>Traceroute mode: probes a single target with increasing TTL and tracks each hop.</summary>
public partial class PlotPingViewModel : MonitorViewModelBase
{
    [ObservableProperty] private string _target;

    // Maintain all rows (1-30) for data collection, even if not displayed
    private readonly Dictionary<int, ProbeRowViewModel> _allRowsByTtl = new();

    public PlotPingViewModel(AppConfig settings, ConfigService configSvc, PingService ping, TracerouteService trace, LogService log)
        : base(settings, configSvc, ping, trace, log)
    {
        _target = settings.Traceroute.PlotPingTarget;
    }

    public override AppMode Mode => AppMode.PlotPing;
    public override string WindowTitle => $"MultiPing — PlotPing (traceroute) : {Target}";

    partial void OnTargetChanged(string value)
    {
        _allRowsByTtl.Clear();
        Rows.Clear();
        Trace.ResetState(value.Trim());
    }

    protected override void OnStarting()
    {
        _allRowsByTtl.Clear();
        Rows.Clear();
        Trace.ResetState(Target.Trim());
        RememberHost(Target);
    }

    protected override async Task RunRoundAsync(CancellationToken ct)
    {
        string target = Target.Trim();
        if (string.IsNullOrEmpty(target)) return;

        // Run traceroute round (adaptive TTL with lookahead)
        var allHops = await Trace.RunRoundAsync(target, Settings.Traceroute.MaxHops, Settings.General.PingTimeoutMs, Settings.Traceroute.LookAheadLimit, ct);

        // Add samples only for hops that were probed this round
        foreach (var hop in allHops)
        {
            ProbeRowViewModel row = GetOrCreateRow(hop.Ttl);
            row.AddSample(new PingSample(DateTime.UtcNow, hop.RttMs));
        }

        // Trim for display: rebuild Rows collection with only relevant hops
        var displayHops = Services.TracerouteService.TrimForDisplay(allHops);
        Rows.Clear();

        foreach (var hop in displayHops)
        {
            ProbeRowViewModel row = GetOrCreateRow(hop.Ttl);

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

            Rows.Add(row);
        }
    }

    private ProbeRowViewModel GetOrCreateRow(int ttl)
    {
        if (!_allRowsByTtl.TryGetValue(ttl, out var row))
        {
            row = new ProbeRowViewModel(ttl, host: "*") { DisplayLabel = $"{ttl}. *", IpAddress = "*" };
            _allRowsByTtl[ttl] = row;
        }
        return row;
    }

    public override void SaveSettings()
    {
        Settings.Traceroute.PlotPingTarget = Target;
        base.SaveSettings();
    }
}
