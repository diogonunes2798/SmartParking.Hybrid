using System.Text.Json;
using SmartParking.SensorSimulator.Wire;

namespace SmartParking.SensorSimulator.Provisioning;

/// <summary>
/// The <c>hardwareId</c>–<c>spotId</c> mapping written by <c>--provision</c> (spec 003, round 5) and read by every
/// emitting run. <see cref="Seed"/> is the provisioning seed, kept so a run can be traced back to its layout.
/// </summary>
internal sealed record SensorMap(int Seed, Guid FacilityId, IReadOnlyList<Guid> ZoneIds, IReadOnlyList<SensorMapEntry> Sensors)
{
    private static readonly JsonSerializerOptions FileJson = new(ApiWire.Json) { WriteIndented = true };

    /// <summary>
    /// Writes to a temporary file next to <paramref name="path"/> and then moves it into place, so a cancelled or
    /// failed write never leaves a truncated mapping (second review of F4b).
    /// </summary>
    public async Task SaveAsync(string path, CancellationToken cancellationToken)
    {
        var temporary = path + ".tmp";
        try
        {
            await using (var stream = File.Create(temporary))
            {
                await JsonSerializer.SerializeAsync(stream, this, FileJson, cancellationToken);
            }

            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            File.Delete(temporary);
        }
    }

    /// <summary>Reads a mapping; <see langword="null"/> when the file holds no mapping.</summary>
    public static async Task<SensorMap?> LoadAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(path);
        return await JsonSerializer.DeserializeAsync<SensorMap>(stream, FileJson, cancellationToken);
    }
}
