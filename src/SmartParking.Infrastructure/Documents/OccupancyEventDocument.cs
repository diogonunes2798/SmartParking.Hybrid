using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;
using SmartParking.Domain.Occupancy;

namespace SmartParking.Infrastructure.Documents;

/// <summary>
/// One document of <c>occupancy_events</c> (spec 003). <c>_id</c> is the <c>eventId</c>, so the mandatory <c>_id</c>
/// index gives idempotency without an extra index. Identifiers are standard UUIDs (subtype 4), set per member rather
/// than by a global convention. Times are UTC BSON dates (millisecond precision). <c>payload</c> is the device's object
/// as received, and is left out when absent.
/// </summary>
internal sealed class OccupancyEventDocument
{
    [BsonId]
    [BsonGuidRepresentation(GuidRepresentation.Standard)]
    public Guid EventId { get; init; }

    [BsonElement("sensorId")]
    [BsonGuidRepresentation(GuidRepresentation.Standard)]
    public Guid SensorId { get; init; }

    [BsonElement("hardwareId")]
    public required string HardwareId { get; init; }

    [BsonElement("spotId")]
    [BsonGuidRepresentation(GuidRepresentation.Standard)]
    public Guid SpotId { get; init; }

    [BsonElement("facilityId")]
    [BsonGuidRepresentation(GuidRepresentation.Standard)]
    public Guid FacilityId { get; init; }

    [BsonElement("zoneId")]
    [BsonGuidRepresentation(GuidRepresentation.Standard)]
    public Guid ZoneId { get; init; }

    [BsonElement("type")]
    [BsonRepresentation(BsonType.String)]
    public OccupancyEventType Type { get; init; }

    [BsonElement("timestamp")]
    [BsonDateTimeOptions(Kind = DateTimeKind.Utc)]
    public DateTime Timestamp { get; init; }

    [BsonElement("receivedAt")]
    [BsonDateTimeOptions(Kind = DateTimeKind.Utc)]
    public DateTime ReceivedAt { get; init; }

    [BsonElement("payload")]
    [BsonIgnoreIfNull]
    public BsonDocument? Payload { get; init; }

    public static OccupancyEventDocument From(OccupancyEvent occupancyEvent)
    {
        ArgumentNullException.ThrowIfNull(occupancyEvent);

        return new OccupancyEventDocument
        {
            EventId = occupancyEvent.EventId,
            SensorId = occupancyEvent.SensorId,
            HardwareId = occupancyEvent.HardwareId,
            SpotId = occupancyEvent.SpotId,
            FacilityId = occupancyEvent.FacilityId,
            ZoneId = occupancyEvent.ZoneId,
            Type = occupancyEvent.Type,
            Timestamp = occupancyEvent.Timestamp.UtcDateTime,
            ReceivedAt = occupancyEvent.ReceivedAt.UtcDateTime,
            Payload = occupancyEvent.Payload is null ? null : BsonDocument.Parse(occupancyEvent.Payload),
        };
    }
}
