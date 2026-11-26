namespace SmartParking.SensorSimulator.Provisioning;

/// <summary>What <c>--provision</c> creates: one facility with <see cref="Zones"/> zones and <see cref="Spots"/> spots, each with an occupancy sensor.</summary>
internal sealed record ProvisionSettings(int Spots, int Zones, int Seed);
