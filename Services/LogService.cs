using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using MultiPing.Models;

namespace MultiPing.Services;

/// <summary>One row to be written to the log, mirroring a grid row.</summary>
public readonly record struct LogEntry(int Index, string IpAddress, SeriesStatistics Stats);

/// <summary>
/// Appends trace rows to a log file while logging is enabled, and can read those files back.
/// Output is fixed-width text that mirrors the on-screen grid (Hop/Destination, IP, RTT, Min, Max, Avg, PL%)
/// with a leading timestamp column. A new file is created each time logging is turned on; entries append
/// until it is turned off.
/// </summary>
public sealed class LogService : IDisposable
{
    /// <summary>Fixed width of the address column. Longer hostnames are truncated to this length.</summary>
    public const int AddressColumnWidth = 24;

    private static readonly Regex DataRowRegex = new(
        @"^(?<ts>\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}\.\d{3})\s+(?<idx>\d+)\s+(?<addr>\S+)\s+(?<rtt>\S+)",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private readonly object _gate = new();
    private StreamWriter? _writer;
    private AppMode _mode;

    public bool IsOpen => _writer is not null;
    public string? CurrentPath { get; private set; }

    /// <summary>Opens a fresh timestamped log file in <paramref name="directory"/> and writes the header.</summary>
    public void Open(AppMode mode, string directory, DateTime nowLocal)
    {
        lock (_gate)
        {
            Close_NoLock();
            _mode = mode;
            Directory.CreateDirectory(directory);

            string name = $"{ModePrefix(mode)}_{nowLocal:yyyyMMdd_HHmmss}.log";
            CurrentPath = Path.Combine(directory, name);

            _writer = new StreamWriter(CurrentPath, append: true) { AutoFlush = true };
            _writer.WriteLine($"# MultiPing {mode} log started {nowLocal:yyyy-MM-dd HH:mm:ss}");
            _writer.WriteLine(Header(mode));
        }
    }

    /// <summary>Writes one round of entries, each stamped with <paramref name="timestampLocal"/>.</summary>
    public void WriteRound(DateTime timestampLocal, IEnumerable<LogEntry> entries)
    {
        lock (_gate)
        {
            if (_writer is null) return;
            foreach (var e in entries)
                _writer.WriteLine(FormatRow(timestampLocal, e));
        }
    }

    public void Close()
    {
        lock (_gate) Close_NoLock();
    }

    private void Close_NoLock()
    {
        if (_writer is not null)
        {
            try { _writer.Flush(); _writer.Dispose(); } catch { /* best effort */ }
            _writer = null;
            CurrentPath = null;
        }
    }

    private string Header(AppMode mode)
    {
        string first = mode == AppMode.PlotPing ? "Hop" : "Dest";
        return $"{Truncate("Timestamp", 23),-23}" +
           $" {Truncate(first, 5),-5}" +
           $" {Truncate("IP Address", AddressColumnWidth),-AddressColumnWidth}" +
           $" {"RTT",9}" +
           $" {"Min",9}" +
           $" {"Max",9}" +
           $" {"Avg",9}" +
           $" {"PL%",7}";
    }

    private static string FormatRow(DateTime ts, LogEntry e)
    {
        var s = e.Stats;
        return $"{ts.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture),-23}" +
           $" {e.Index.ToString(CultureInfo.InvariantCulture),-5}" +
           $" {PadOrTruncate(e.IpAddress, AddressColumnWidth),-AddressColumnWidth}" +
           $" {Num(s.Last),9}" +
           $" {Num(s.Min),9}" +
           $" {Num(s.Max),9}" +
           $" {Num(s.Avg),9}" +
           $" {s.PacketLossPercent.ToString("0.0", CultureInfo.InvariantCulture),7}";
    }

    private static string Num(double? v) =>
        v is double d ? d.ToString("0.0", CultureInfo.InvariantCulture) : "-";

    private static string PadOrTruncate(string s, int width) =>
        s.Length > width ? s[..width] : s;

    private static string Truncate(string s, int width) =>
        s.Length > width ? s[..width] : s;
    
    public void Dispose() => Close();

    /// <summary>
    /// Reads MultiPing log files in <paramref name="directory"/> and returns the probes whose timestamp
    /// falls within [<paramref name="startUtc"/>, <paramref name="endUtc"/>], grouped by logged address.
    /// Files that ended before the window, or that start after it, are skipped. A missing directory or an
    /// unreadable file yields whatever could be parsed; history is best-effort.
    /// </summary>
    public static Dictionary<string, List<PingSample>> ReadMultiPingSamples(string directory, DateTime startUtc, DateTime endUtc)
    {
        var byAddress = new Dictionary<string, List<PingSample>>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory) || endUtc < startUtc)
            return byAddress;

        string[] files;
        try
        {
            files = Directory.GetFiles(directory, ModePrefix(AppMode.MultiPing) + "_*.log");
        }
        catch
        {
            return byAddress;
        }

        Array.Sort(files, StringComparer.OrdinalIgnoreCase);
        foreach (string path in files)
        {
            try
            {
                if (!FileMayOverlap(path, startUtc, endUtc))
                    continue;
                ReadFile(path, startUtc, endUtc, byAddress);
            }
            catch
            {
                // One locked or truncated log should not discard the rest of the history.
            }
        }

        return byAddress;
    }

    private static string ModePrefix(AppMode mode) =>
        mode == AppMode.PlotPing ? "plotping" : "multiping";

    private static bool FileMayOverlap(string path, DateTime startUtc, DateTime endUtc)
    {
        var info = new FileInfo(path);
        if (info.LastWriteTimeUtc < startUtc)
            return false;

        // multiping_yyyyMMdd_HHmmss — the stamp is the local time logging started.
        string name = Path.GetFileNameWithoutExtension(path);
        int split = name.IndexOf('_');
        if (split < 0 || split >= name.Length - 1)
            return true;

        if (!DateTime.TryParseExact(
                name[(split + 1)..],
                "yyyyMMdd_HHmmss",
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out DateTime localStart))
            return true;

        DateTime fileStartUtc = DateTime.SpecifyKind(localStart, DateTimeKind.Local).ToUniversalTime();
        return fileStartUtc <= endUtc;
    }

    private static void ReadFile(string path, DateTime startUtc, DateTime endUtc, Dictionary<string, List<PingSample>> byAddress)
    {
        var seen = new Dictionary<string, HashSet<DateTime>>(StringComparer.OrdinalIgnoreCase);
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(stream);
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            if (line.Length < 32 || !char.IsDigit(line[0]))
                continue;
            if (!TryParseDataRow(line, out DateTime timestampUtc, out string address, out double? rttMs))
                continue;
            if (timestampUtc < startUtc)
                continue;
            // Rows are appended in time order, so nothing after the window can fall back into it.
            if (timestampUtc > endUtc)
                return;
            if (address.Length == 0 || address == "*")
                continue;

            if (!seen.TryGetValue(address, out HashSet<DateTime>? stamps))
            {
                stamps = new HashSet<DateTime>();
                seen[address] = stamps;
            }
            if (!stamps.Add(timestampUtc))
                continue;

            if (!byAddress.TryGetValue(address, out List<PingSample>? samples))
            {
                samples = new List<PingSample>();
                byAddress[address] = samples;
            }
            samples.Add(new PingSample(timestampUtc, rttMs));
        }
    }

    private static bool TryParseDataRow(string line, out DateTime timestampUtc, out string address, out double? rttMs)
    {
        timestampUtc = default;
        address = string.Empty;
        rttMs = null;

        Match match = DataRowRegex.Match(line);
        if (!match.Success)
            return false;

        if (!DateTime.TryParseExact(
                match.Groups["ts"].Value,
                "yyyy-MM-dd HH:mm:ss.fff",
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeLocal,
                out DateTime local))
            return false;

        timestampUtc = local.ToUniversalTime();
        address = match.Groups["addr"].Value;

        string rttText = match.Groups["rtt"].Value;
        if (rttText == "-")
            return true;

        if (!double.TryParse(rttText, NumberStyles.Float, CultureInfo.InvariantCulture, out double rtt))
            return false;

        rttMs = rtt;
        return true;
    }
}
