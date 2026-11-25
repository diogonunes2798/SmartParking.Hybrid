using SmartParking.Application.Features.Spots;
using SmartParking.Domain.Facilities;

namespace SmartParking.Api.Features.Spots;

/// <summary>Body of <c>POST /api/facilities/{facilityId}/spots/batch</c>: 1 to 1 000 spots, each with an optional sensor.</summary>
internal sealed record SpotBatchRequest(IReadOnlyList<SpotBatchItemRequest>? Spots)
{
    public CreateSpotBatch.Command ToCommand(Guid facilityId) => new(
        facilityId,
        [.. Spots!.Select(item => new CreateSpotBatch.Item(
            item.ZoneId,
            item.Code!,
            Enum.Parse<SpotType>(item.Type!),
            item.Sensor is { } sensor
                ? new CreateSpotBatch.SensorInput(sensor.HardwareId!, sensor.Model!, sensor.InstalledAt!.Value)
                : null))]);
}
