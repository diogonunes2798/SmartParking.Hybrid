namespace SmartParking.SensorSimulator.Provisioning;

/// <summary>One occupancy sensor of the mapping: its <c>hardwareId</c> and the spot, zone and facility it is placed on.</summary>
internal sealed record SensorMapEntry(string HardwareId, Guid SpotId, Guid ZoneId, Guid FacilityId);
