using System;
using System.Collections.Generic;
using System.Linq;
using Timer = robotManager.Helpful.Timer;

namespace Wholesome_Auto_Quester.Helpers
{
    /// <summary>
    /// Lightweight per-label timing aggregator for the dev/perf pass. Records samples (ms) per label and
    /// periodically logs a p95/max summary, so we can SEE which FSM states and background loops are
    /// expensive BEFORE moving heavy work off the 10 ms engine thread (Phase 1). Gated behind the existing
    /// AllowStopWatch setting (off by default = zero overhead). Thread-safe: the FSM, scanner and
    /// task-manager threads all record concurrently.
    /// </summary>
    public static class PerfLog
    {
        private const int WindowSize = 256;
        private const int DumpIntervalMs = 30 * 1000;

        private class Stat
        {
            public long Count;
            public long Sum;
            public long Max;
            public readonly long[] Window = new long[WindowSize];
            public int Filled;
            private int _next;

            public void Add(long ms)
            {
                Count++;
                Sum += ms;
                if (ms > Max) Max = ms;
                Window[_next] = ms;
                _next = (_next + 1) % WindowSize;
                if (Filled < WindowSize) Filled++;
            }

            // p95 over the rolling window (true max is tracked separately and exactly).
            public long Percentile(double p)
            {
                if (Filled == 0) return 0;
                long[] copy = new long[Filled];
                Array.Copy(Window, copy, Filled);
                Array.Sort(copy);
                int idx = (int)Math.Ceiling(p * Filled) - 1;
                if (idx < 0) idx = 0;
                if (idx >= Filled) idx = Filled - 1;
                return copy[idx];
            }
        }

        private static readonly Dictionary<string, Stat> _stats = new Dictionary<string, Stat>();
        private static readonly object _lock = new object();
        private static Timer _dumpTimer = new Timer(DumpIntervalMs);

        public static void Record(string label, long ms)
        {
            if (!WholesomeAQSettings.CurrentSetting.AllowStopWatch || string.IsNullOrEmpty(label))
                return;
            lock (_lock)
            {
                if (!_stats.TryGetValue(label, out Stat stat))
                {
                    stat = new Stat();
                    _stats[label] = stat;
                }
                stat.Add(ms);
            }
        }

        /// <summary>
        /// Once every <see cref="DumpIntervalMs"/>, log a summary (top entries by max) and reset. Cheap to
        /// call every loop iteration; it no-ops until the interval elapses. Drive it from one background loop.
        /// </summary>
        public static void DumpIfDue()
        {
            if (!WholesomeAQSettings.CurrentSetting.AllowStopWatch || !_dumpTimer.IsReady)
                return;
            _dumpTimer = new Timer(DumpIntervalMs);

            string summary;
            lock (_lock)
            {
                if (_stats.Count <= 0)
                    return;
                summary = string.Join("  |  ", _stats
                    .OrderByDescending(kv => kv.Value.Max)
                    .Take(12)
                    .Select(kv =>
                    {
                        Stat s = kv.Value;
                        long avg = s.Count > 0 ? s.Sum / s.Count : 0;
                        return $"{kv.Key} n={s.Count} avg={avg} p95={s.Percentile(0.95)} max={s.Max}";
                    }));
                _stats.Clear();
            }
            Logger.Log($"[PERF ~30s] {summary}");
        }
    }
}
