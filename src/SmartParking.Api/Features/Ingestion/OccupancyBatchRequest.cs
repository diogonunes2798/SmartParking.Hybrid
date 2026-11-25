using System.Globalization;
using System.Text.Json;
using SmartParking.Application.Features.Ingestion;
using SmartParking.Domain.Occupancy;

namespace SmartParking.Api.Features.Ingestion;

/// <summary>A gateway batch of 1 to 500 events (spec 003). A single event is a batch of one.</summary>
internal sealed record OccupancyBatchRequest(IReadOnlyList<OccupancyEventRequest>? Events)
{
    /// <summary>Only called after <see cref="OccupancyBatchRequestValidator"/> passed, so every value parses.</summary>
    public IngestOccupancyBatch.Command ToCommand() => new(
    [
        .. Events!.Select(item => new IngestOccupancyBatch.Item(
            Guid.Parse(item.EventId!, CultureInfo.InvariantCulture),
            item.SensorId!,
            Guid.Parse(item.SpotId!, CultureInfo.InvariantCulture),
            Enum.Parse<OccupancyEventType>(item.Type!),
            DateTimeOffset.Parse(item.Timestamp!, CultureInfo.InvariantCulture, DateTimeStyles.None),
            item.Payload is { ValueKind: JsonValueKind.Object } payload ? payload.GetRawText() : null)),
    ]);
}
