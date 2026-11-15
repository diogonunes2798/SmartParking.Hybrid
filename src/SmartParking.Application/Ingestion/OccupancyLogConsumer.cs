namespace SmartParking.Application.Ingestion;

/// <summary>Identity of the consumer group that writes the occupancy event log (UC-01 step 3).</summary>
public static class OccupancyLogConsumer
{
    /// <summary>Configuration name (<c>Messaging:Consumers:occupancy-log</c>) and consumer group name.</summary>
    public const string Name = "occupancy-log";
}
