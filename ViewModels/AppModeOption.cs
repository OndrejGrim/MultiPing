using System.Collections.Generic;
using System.Linq;
using MultiPing.Models;

namespace MultiPing.ViewModels;

/// <summary>Display wrapper for an <see cref="AppMode"/> value, used by the Options dialog combo box.</summary>
public sealed class AppModeOption
{
    public static IReadOnlyList<AppModeOption> All { get; } =
    [
        new(AppMode.PlotPing, "PlotPing (traceroute)"),
        new(AppMode.MultiPing, "MultiPing (multiple targets)")
    ];

    public AppModeOption(AppMode mode, string label)
    {
        Mode = mode;
        Label = label;
    }

    public AppMode Mode { get; }
    public string Label { get; }

    public static AppModeOption For(AppMode mode) =>
        All.FirstOrDefault(option => option.Mode == mode) ?? All[0];

    public override string ToString() => Label;
}
