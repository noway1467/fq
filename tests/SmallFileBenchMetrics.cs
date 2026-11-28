using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Web.Script.Serialization;

namespace FeiqLight
{
    // 只编入基准的临时源码副本，交付程序不带计时器或遥测。
    internal static class SmallFileBenchMetrics
    {
        private static readonly object gate = new object();
        private static readonly Dictionary<string, double> segments = new Dictionary<string, double>();
        private static readonly Dictionary<string, int> counts = new Dictionary<string, int>();
        private static readonly HashSet<string> stored = new HashSet<string>();
        private static bool enabled;
        private static double cpuStart;
        internal static long Stamp() { return Stopwatch.GetTimestamp(); }
        internal static int StoredCount { get { lock (gate) return stored.Count; } }
        internal static void Stored(string key) { lock (gate) if (enabled) stored.Add(key); }
        internal static void Add(string name, long started)
        {
            lock (gate)
            {
                if (!enabled) return;
                double previous; int count;
                segments.TryGetValue(name, out previous); counts.TryGetValue(name, out count);
                segments[name] = previous + (Stamp() - started) * 1000.0 / Stopwatch.Frequency;
                counts[name] = count + 1;
            }
        }
        internal static void Start()
        {
            lock (gate)
            {
                segments.Clear(); counts.Clear(); stored.Clear();
                cpuStart = Process.GetCurrentProcess().TotalProcessorTime.TotalMilliseconds;
                enabled = true;
            }
        }
        internal static void Write(string path, double elapsed, List<double> latencies)
        {
            lock (gate)
            {
                enabled = false;
                var result = new {
                    segmentsMs = segments, counts = counts, storedFiles = stored.Count, elapsedMs = elapsed, latenciesMs = latencies,
                    cpuMs = Process.GetCurrentProcess().TotalProcessorTime.TotalMilliseconds - cpuStart,
                    managedBytes = GC.GetTotalMemory(false)
                };
                File.WriteAllText(path, new JavaScriptSerializer().Serialize(result), new UTF8Encoding(false));
            }
        }
    }
}
