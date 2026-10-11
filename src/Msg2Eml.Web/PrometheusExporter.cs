using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Globalization;
using System.Text;

namespace Msg2Eml.Web;

/// <summary>
/// Minimal Prometheus text exposition (format 0.0.4) for the <see cref="ConversionMetrics"/>
/// meter plus a few process gauges, without third-party packages. Counters and up/down
/// counters are summed, histograms use fixed buckets chosen per instrument.
/// </summary>
public sealed class PrometheusExporter : IDisposable
{
    private static readonly double[] DurationBuckets = [0.05, 0.1, 0.25, 0.5, 1, 2.5, 5, 10, 30];
    private static readonly double[] SizeBuckets = [10_000, 100_000, 1_000_000, 5_000_000, 10_000_000, 25_000_000, 50_000_000, 100_000_000];

    private readonly MeterListener _listener = new();
    private readonly ConcurrentDictionary<string, Series> _series = new();
    private readonly Stopwatch _uptime = Stopwatch.StartNew();

    public PrometheusExporter()
    {
        _listener.InstrumentPublished = (instrument, listener) =>
        {
            if (instrument.Meter.Name == ConversionMetrics.MeterName) listener.EnableMeasurementEvents(instrument);
        };
        _listener.SetMeasurementEventCallback<long>((inst, value, tags, _) => Record(inst, value, tags));
        _listener.SetMeasurementEventCallback<double>((inst, value, tags, _) => Record(inst, value, tags));
        _listener.SetMeasurementEventCallback<int>((inst, value, tags, _) => Record(inst, value, tags));
        _listener.Start();
    }

    private void Record(Instrument instrument, double value, ReadOnlySpan<KeyValuePair<string, object?>> tags)
    {
        var labels = Labels(tags);
        var key = instrument.Name + "|" + labels;
        var series = _series.GetOrAdd(key, _ => new Series(instrument, labels));
        series.Record(value);
    }

    public string Render()
    {
        var sb = new StringBuilder();
        var byInstrument = _series.Values.GroupBy(s => s.Instrument.Name).OrderBy(g => g.Key);

        foreach (var group in byInstrument)
        {
            var inst = group.First().Instrument;
            var name = PromName(inst);
            var type = inst switch
            {
                Histogram<double> or Histogram<long> or Histogram<int> => "histogram",
                UpDownCounter<long> or UpDownCounter<int> or UpDownCounter<double> => "gauge",
                _ => "counter",
            };
            if (type == "counter") name += "_total";

            sb.Append("# HELP ").Append(name).Append(' ').Append(inst.Description).Append('\n');
            sb.Append("# TYPE ").Append(name).Append(' ').Append(type).Append('\n');

            foreach (var s in group.OrderBy(s => s.LabelsText))
            {
                lock (s)
                {
                    if (type == "histogram")
                    {
                        var buckets = s.Buckets!;
                        var cumulative = 0L;
                        for (var i = 0; i < buckets.Length; i++)
                        {
                            cumulative += s.BucketCounts![i];
                            sb.Append(name).Append("_bucket").Append(WithLabel(s.LabelsText, "le", Fmt(buckets[i]))).Append(' ').Append(cumulative).Append('\n');
                        }
                        sb.Append(name).Append("_bucket").Append(WithLabel(s.LabelsText, "le", "+Inf")).Append(' ').Append(s.Count).Append('\n');
                        sb.Append(name).Append("_sum").Append(Wrap(s.LabelsText)).Append(' ').Append(Fmt(s.Sum)).Append('\n');
                        sb.Append(name).Append("_count").Append(Wrap(s.LabelsText)).Append(' ').Append(s.Count).Append('\n');
                    }
                    else
                    {
                        sb.Append(name).Append(Wrap(s.LabelsText)).Append(' ').Append(Fmt(s.Sum)).Append('\n');
                    }
                }
            }
        }

        using var proc = Process.GetCurrentProcess();
        Gauge(sb, "process_resident_memory_bytes", "Resident memory in bytes", proc.WorkingSet64);
        Gauge(sb, "process_uptime_seconds", "Seconds since the process started", _uptime.Elapsed.TotalSeconds);
        Gauge(sb, "dotnet_gc_heap_bytes", "Total managed heap in bytes", GC.GetTotalMemory(false));
        Gauge(sb, "dotnet_gc_collections_total", "Gen 2 garbage collections", GC.CollectionCount(2));
        Gauge(sb, "dotnet_threadpool_threads", "Thread pool threads", ThreadPool.ThreadCount);

        return sb.ToString();
    }

    private static void Gauge(StringBuilder sb, string name, string help, double value)
    {
        sb.Append("# HELP ").Append(name).Append(' ').Append(help).Append('\n');
        sb.Append("# TYPE ").Append(name).Append(" gauge\n");
        sb.Append(name).Append(' ').Append(Fmt(value)).Append('\n');
    }

    private static string PromName(Instrument inst) => inst.Name.Replace('.', '_').Replace('-', '_') + inst.Unit switch
    {
        "s" => "_seconds",
        "By" => "_bytes",
        _ => "",
    };

    private static string Labels(ReadOnlySpan<KeyValuePair<string, object?>> tags)
    {
        if (tags.Length == 0) return "";
        var parts = new List<string>(tags.Length);
        foreach (var t in tags)
            parts.Add($"{t.Key}=\"{Escape(t.Value?.ToString() ?? "")}\"");
        parts.Sort(StringComparer.Ordinal);
        return string.Join(",", parts);
    }

    private static string Wrap(string labels) => labels.Length == 0 ? "" : "{" + labels + "}";
    private static string WithLabel(string labels, string key, string value) =>
        "{" + (labels.Length == 0 ? "" : labels + ",") + key + "=\"" + value + "\"}";
    private static string Escape(string s) => s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n");
    private static string Fmt(double v) => v.ToString("0.############", CultureInfo.InvariantCulture);

    public void Dispose() => _listener.Dispose();

    private sealed class Series
    {
        public Instrument Instrument { get; }
        public string LabelsText { get; }
        public double Sum;
        public long Count;
        public double[]? Buckets;
        public long[]? BucketCounts;

        public Series(Instrument instrument, string labels)
        {
            Instrument = instrument;
            LabelsText = labels;
            if (instrument is Histogram<double> or Histogram<long> or Histogram<int>)
            {
                Buckets = instrument.Unit == "s" ? DurationBuckets : SizeBuckets;
                BucketCounts = new long[Buckets.Length];
            }
        }

        public void Record(double value)
        {
            lock (this)
            {
                Sum += value;
                Count++;
                if (Buckets == null) return;
                for (var i = 0; i < Buckets.Length; i++)
                    if (value <= Buckets[i]) { BucketCounts![i]++; break; }
            }
        }
    }
}
