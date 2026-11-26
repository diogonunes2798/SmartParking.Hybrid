namespace SmartParking.SensorSimulator.Wire;

/// <summary>The only field the simulator reads from a created facility or zone: its id.</summary>
internal sealed record CreatedResource(Guid Id);
