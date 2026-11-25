using SmartParking.Application.Abstractions;
using SmartParking.Application.Abstractions.Messaging;
using SmartParking.Application.Abstractions.Reading;

namespace SmartParking.Application.Features.Sensors;

/// <summary>Reads one sensor with its version (ETag); 404 when it does not exist.</summary>
public static class GetSensor
{
    public sealed record Query(Guid Id);

    internal sealed class Handler(ISensorReader reader) : IQueryHandler<Query, SensorView>
    {
        public async Task<Result<SensorView>> HandleAsync(Query query, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(query);

            var sensor = await reader.GetAsync(query.Id, cancellationToken);
            return sensor is null ? SensorErrors.NotFound(query.Id) : sensor;
        }
    }
}
