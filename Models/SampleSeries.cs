using System;
using System.Collections.Generic;

namespace MultiPing.Models;

/// <summary>
/// A thread-safe rolling buffer of <see cref="PingSample"/> values for one probe target (a hop or a destination IP).
/// Maintains running statistics and can produce time-sliced data for plotting.
/// Probe results arrive on background threads, so all access is guarded by a lock.
/// </summary>
public sealed class SampleSeries
{
    private readonly object _gate = new();
    private readonly List<PingSample> _samples = new();
    private readonly int _maxSamples;

    private int _received;
    private int _lost;
    private double _sum;
    private double _min = double.NaN;
    private double _max = double.NaN;

    public SampleSeries(int maxSamples = 200_000)
    {
        _maxSamples = maxSamples;
    }

    public void Add(PingSample sample)
    {
        lock (_gate)
        {
            Add_NoLock(sample);
            Trim_NoLock();
        }
    }

    /// <summary>
    /// Inserts samples in timestamp order, skipping any timestamp already stored.
    /// Returns the number of samples actually added. Running stats include the new samples.
    /// </summary>
    public int Merge(IEnumerable<PingSample> samples)
    {
        lock (_gate)
        {
            var seen = new HashSet<DateTime>(_samples.Count);
            foreach (PingSample existing in _samples)
                seen.Add(existing.TimestampUtc);

            int added = 0;
            foreach (PingSample sample in samples)
            {
                if (!seen.Add(sample.TimestampUtc))
                    continue;
                Add_NoLock(sample);
                added++;
            }

            if (added == 0)
                return 0;

            _samples.Sort(static (a, b) => a.TimestampUtc.CompareTo(b.TimestampUtc));
            Trim_NoLock();
            return added;
        }
    }

    private void Add_NoLock(PingSample sample)
    {
        _samples.Add(sample);

        if (sample.RttMs is double rtt)
        {
            _received++;
            _sum += rtt;
            if (double.IsNaN(_min) || rtt < _min) _min = rtt;
            if (double.IsNaN(_max) || rtt > _max) _max = rtt;
        }
        else
        {
            _lost++;
        }
    }

    private void Trim_NoLock()
    {
        // Trim oldest samples once we exceed the cap. Running stats intentionally
        // reflect the whole session, not just the retained window.
        if (_samples.Count <= _maxSamples)
            return;

        int remove = _samples.Count - _maxSamples;
        _samples.RemoveRange(0, remove);
    }

    public SeriesStatistics Snapshot()
    {
        lock (_gate)
        {
            int total = _received + _lost;
            double? last = _samples.Count > 0 ? _samples[^1].RttMs : null;
            return new SeriesStatistics(
                Last: last,
                Min: double.IsNaN(_min) ? null : _min,
                Max: double.IsNaN(_max) ? null : _max,
                Avg: _received > 0 ? _sum / _received : null,
                Sent: total,
                Lost: _lost,
                PacketLossPercent: total > 0 ? _lost * 100.0 / total : 0.0);
        }
    }

    /// <summary>
    /// Returns the samples whose timestamp falls within [<paramref name="startUtc"/>, <paramref name="endUtc"/>].
    /// Lost samples are represented as NaN in <paramref name="values"/> so plots show gaps.
    /// </summary>
    public void GetWindow(DateTime startUtc, DateTime endUtc, out double[] times, out double[] values)
    {
        lock (_gate)
        {
            var t = new List<double>(_samples.Count);
            var v = new List<double>(_samples.Count);
            foreach (var s in _samples)
            {
                if (s.TimestampUtc < startUtc || s.TimestampUtc > endUtc) continue;
                t.Add(s.TimestampUtc.ToOADate());
                v.Add(s.RttMs ?? double.NaN);
            }
            times = t.ToArray();
            values = v.ToArray();
        }
    }

    /// <summary>The timestamp range currently held, or null if empty. Used to bound scrolling.</summary>
    public (DateTime Oldest, DateTime Newest)? Extent()
    {
        lock (_gate)
        {
            if (_samples.Count == 0) return null;
            return (_samples[0].TimestampUtc, _samples[^1].TimestampUtc);
        }
    }
}

/// <summary>Immutable snapshot of a series' aggregate statistics for display in the grid.</summary>
public readonly record struct SeriesStatistics(
    double? Last,
    double? Min,
    double? Max,
    double? Avg,
    int Sent,
    int Lost,
    double PacketLossPercent);
