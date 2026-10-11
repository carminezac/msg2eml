using System.Diagnostics.Metrics;
using Msg2Eml.Core;

namespace Msg2Eml.Web;

/// <summary>
/// Conversion metrics (plan, section 13) on the standard <see cref="System.Diagnostics.Metrics"/>
/// API: readable with <c>dotnet-counters</c>, any OpenTelemetry collector, and exposed in
/// Prometheus format by <see cref="PrometheusExporter"/>. No tag ever carries message content.
/// </summary>
public sealed class ConversionMetrics : IDisposable
{
    public const string MeterName = "Msg2Eml";

    private readonly Meter _meter;
    private readonly Counter<long> _conversions;
    private readonly Histogram<double> _duration;
    private readonly Histogram<long> _inputBytes;
    private readonly Histogram<long> _outputBytes;
    private readonly UpDownCounter<long> _inProgress;

    public ConversionMetrics()
    {
        _meter = new Meter(MeterName, typeof(ConversionMetrics).Assembly.GetName().Version?.ToString(3));
        _conversions = _meter.CreateCounter<long>("msg2eml.conversions", "{conversion}", "Conversions by result and error code");
        _duration = _meter.CreateHistogram<double>("msg2eml.conversion.duration", "s", "Conversion duration in seconds");
        _inputBytes = _meter.CreateHistogram<long>("msg2eml.input.size", "By", "Size of the uploaded .msg");
        _outputBytes = _meter.CreateHistogram<long>("msg2eml.output.size", "By", "Size of the produced .eml");
        _inProgress = _meter.CreateUpDownCounter<long>("msg2eml.conversions.in_progress", "{conversion}", "Conversions currently running");
    }

    public void Started(long inputBytes)
    {
        _inProgress.Add(1);
        _inputBytes.Record(inputBytes);
    }

    public void Completed(TimeSpan elapsed, long outputBytes)
    {
        _inProgress.Add(-1);
        _duration.Record(elapsed.TotalSeconds, new KeyValuePair<string, object?>("result", "success"));
        _outputBytes.Record(outputBytes);
        _conversions.Add(1, new KeyValuePair<string, object?>("result", "success"), new KeyValuePair<string, object?>("error_code", ""));
    }

    public void Failed(TimeSpan elapsed, ConversionErrorCode code)
    {
        _inProgress.Add(-1);
        _duration.Record(elapsed.TotalSeconds, new KeyValuePair<string, object?>("result", "error"));
        _conversions.Add(1, new KeyValuePair<string, object?>("result", "error"), new KeyValuePair<string, object?>("error_code", code.ToString()));
    }

    public void Rejected(string reason)
    {
        _conversions.Add(1, new KeyValuePair<string, object?>("result", "rejected"), new KeyValuePair<string, object?>("error_code", reason));
    }

    public void Dispose() => _meter.Dispose();
}
