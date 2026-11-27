using System.Diagnostics.Metrics;
using Microsoft.Extensions.DependencyInjection;

namespace SmartParking.Api.Tests.Fixtures;

/// <summary>
/// Records the measurements of one meter of one test host (spec 003, AC-17; spec 004, AC-22), with the BCL
/// <see cref="MeterListener"/>. Other hosts in the process create their own instance of the same meter name, so only
/// the instruments of this host's <see cref="IMeterFactory"/> are listened to.
/// </summary>
internal sealed class MeterRecorder : IDisposable
{
    private readonly MeterListener listener;
    private readonly List<Measurement> measurements = [];

    public MeterRecorder(IServiceProvider services, string meterName)
    {
        var meterFactory = services.GetRequiredService<IMeterFactory>();
        listener = new MeterListener
        {
            InstrumentPublished = (instrument, meterListener) =>
            {
                if (instrument.Meter.Name == meterName && ReferenceEquals(instrument.Meter.Scope, meterFactory))
                {
                    meterListener.EnableMeasurementEvents(instrument);
                }
            },
        };
        listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) => Record(instrument, value, tags));
        listener.SetMeasurementEventCallback<int>((instrument, value, tags, _) => Record(instrument, value, tags));
        listener.SetMeasurementEventCallback<double>((instrument, value, tags, _) => Record(instrument, value, tags));
        listener.Start();
    }

    public sealed record Measurement(string Instrument, double Value, IReadOnlyDictionary<string, object?> Tags);

    public List<Measurement> Snapshot()
    {
        lock (measurements)
        {
            return [.. measurements];
        }
    }

    public void Dispose() => listener.Dispose();

    private void Record(Instrument instrument, double value, ReadOnlySpan<KeyValuePair<string, object?>> tags)
    {
        var copy = tags.ToArray().ToDictionary(tag => tag.Key, tag => tag.Value);
        lock (measurements)
        {
            measurements.Add(new Measurement(instrument.Name, value, copy));
        }
    }
}
