using System.Diagnostics;
using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SmartParking.SensorSimulator;

/// <summary>
/// Creates, through the RF-06 API, the facilities, spots and sensors that the scenario needs, and reuses those a
/// previous run created (found by their deterministic names). The read model ignores events of spots it does not
/// know (<c>ApplyOutcome.UnknownSpot</c>, ADR-0008), so <see cref="WaitForReadModelAsync"/> waits until the
/// configuration events have gone through the outbox relay (Phase 5) and the configuration projector (Phase 4).
/// Going through the API, and not a SQL seed, exercises the same path an operator would use.
/// </summary>
internal sealed class Provisioner(HttpClient http, ScenarioOptions options, TextWriter log)
{
    private const int PageSize = 200; // PageRequest.MaxTake
    private const int ParallelCreates = 8;

    private readonly Dictionary<int, (double Latitude, double Longitude)> _locations = [];

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    public async Task<Topology> ProvisionAsync(CancellationToken ct)
    {
        var watch = Stopwatch.StartNew();
        var facilityOfSpot = Topology.MapSpotsToFacilities(options.Facilities, options.Spots);
        var facilityIds = new Guid[options.Facilities];
        var spotIds = new Guid[options.Spots];
        var sensorIds = new Guid[options.Spots];

        var existingFacilities = await ListAllAsync<FacilityDto>("/api/facilities", ct);
        var byName = existingFacilities.GroupBy(f => f.Name, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        var created = new Counts();
        _locations.Clear();

        var firstSpot = 0;
        for (var f = 0; f < options.Facilities; f++)
        {
            var count = Topology.SpotsIn(f, options.Facilities, options.Spots);
            var name = Topology.FacilityName(options.Name, f);
            if (!byName.TryGetValue(name, out var facility))
            {
                facility = await CreateFacilityAsync(f, name, count, ct);
                created.Facilities++;
            }

            facilityIds[f] = facility.Id;
            _locations[f] = (facility.Location.Coordinates[1], facility.Location.Coordinates[0]); // GeoJSON: lon, lat
            await ProvisionSpotsAsync(facility.Id, firstSpot, count, spotIds, created, ct);
            firstSpot += count;
        }

        await ProvisionSensorsAsync(spotIds, sensorIds, created, ct);

        log.WriteLine(string.Create(
            CultureInfo.InvariantCulture,
            $"Provisioned {options.Facilities} facilities, {options.Spots} spots and sensors in {watch.Elapsed.TotalSeconds:F1} s " +
            $"(created: {created.Facilities} facilities, {created.Spots} spots, {created.Sensors} sensors)."));
        return new Topology(facilityIds, spotIds, sensorIds, facilityOfSpot);
    }

    /// <summary>
    /// Waits until <c>GET /api/availability</c> reports every simulated spot of every facility. Returns false on
    /// timeout (the Workers are probably not running).
    /// </summary>
    public async Task<bool> WaitForReadModelAsync(Topology topology, CancellationToken ct)
    {
        if (options.WaitReadModelSeconds == 0)
        {
            return true;
        }

        var deadline = Stopwatch.StartNew();
        var pending = Enumerable.Range(0, topology.FacilityIds.Length).ToHashSet();
        while (pending.Count > 0)
        {
            foreach (var f in pending.ToArray())
            {
                // The facility's actual location: one created by an older version of the simulator may sit elsewhere.
                var (lat, lon) = _locations[f];
                var url = string.Create(CultureInfo.InvariantCulture, $"/api/availability?lat={lat}&lon={lon}&radius=50&limit=200");
                var found = await http.GetFromJsonAsync<List<FacilityAvailabilityDto>>(url, Json, ct) ?? [];
                var expected = Topology.SpotsIn(f, options.Facilities, options.Spots);
                if (found.Any(a => a.FacilityId == topology.FacilityIds[f] && a.Spots >= expected))
                {
                    pending.Remove(f);
                }
            }

            if (pending.Count == 0)
            {
                break;
            }

            if (deadline.Elapsed.TotalSeconds >= options.WaitReadModelSeconds)
            {
                log.WriteLine($"Read model still misses spots of {pending.Count} facilities after {options.WaitReadModelSeconds} s. " +
                              "Are SmartParking.Workers running (outbox relay and configuration projector)?");
                return false;
            }

            await Task.Delay(TimeSpan.FromSeconds(1), ct);
        }

        log.WriteLine(string.Create(CultureInfo.InvariantCulture, $"Read model knows every simulated spot ({deadline.Elapsed.TotalSeconds:F1} s)."));
        return true;
    }

    private async Task<FacilityDto> CreateFacilityAsync(int f, string name, int capacity, CancellationToken ct)
    {
        EnsureMayCreate($"facility '{name}'");
        var (lat, lon) = Topology.Location(options.Name, f);
        var body = new
        {
            name,
            location = new { type = "Point", coordinates = new[] { lon, lat } },
            capacity,
            type = "Uncovered",
            @operator = "SensorSimulator",
            zone = Topology.Zone(options.Name, f),
        };
        return await PostAsync<FacilityDto>("/api/facilities", body, ct);
    }

    private async Task ProvisionSpotsAsync(Guid facilityId, int firstSpot, int count, Guid[] spotIds, Counts created, CancellationToken ct)
    {
        var existing = (await ListAllAsync<SpotDto>($"/api/facilities/{facilityId}/spots", ct))
            .GroupBy(s => s.Code, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First().Id, StringComparer.Ordinal);

        await Parallel.ForAsync(0, count, new ParallelOptions { MaxDegreeOfParallelism = ParallelCreates, CancellationToken = ct }, async (j, token) =>
        {
            var code = Topology.SpotCode(j);
            if (!existing.TryGetValue(code, out var id))
            {
                EnsureMayCreate($"spot '{code}' of facility {facilityId}");
                id = (await PostAsync<SpotDto>($"/api/facilities/{facilityId}/spots", new { code, allowedVehicleType = "Car" }, token)).Id;
                Interlocked.Increment(ref created.Spots);
            }

            spotIds[firstSpot + j] = id;
        });
    }

    /// <summary>One active sensor per spot. A spot with several sensors keeps the first one listed.</summary>
    private async Task ProvisionSensorsAsync(Guid[] spotIds, Guid[] sensorIds, Counts created, CancellationToken ct)
    {
        var wanted = spotIds.ToHashSet();
        var existing = new Dictionary<Guid, Guid>();
        foreach (var sensor in await ListAllAsync<SensorDto>("/api/sensors", ct))
        {
            if (wanted.Contains(sensor.SpotId))
            {
                existing.TryAdd(sensor.SpotId, sensor.Id);
            }
        }

        await Parallel.ForAsync(0, spotIds.Length, new ParallelOptions { MaxDegreeOfParallelism = ParallelCreates, CancellationToken = ct }, async (i, token) =>
        {
            if (!existing.TryGetValue(spotIds[i], out var id))
            {
                EnsureMayCreate($"the sensor of spot {spotIds[i]}");
                var body = new { spotId = spotIds[i], model = "SIM-OCC", firmwareVersion = "1.0.0", status = "Active" };
                id = (await PostAsync<SensorDto>("/api/sensors", body, token)).Id;
                Interlocked.Increment(ref created.Sensors);
            }

            sensorIds[i] = id;
        });
    }

    /// <summary><c>--provision false</c>: only look up what exists, and stop if anything is missing (review M3).</summary>
    private void EnsureMayCreate(string what)
    {
        if (!options.Provision)
        {
            throw new InvalidOperationException($"Missing {what} and --provision is false. Run once with --provision true.");
        }
    }

    private async Task<List<T>> ListAllAsync<T>(string path, CancellationToken ct)
    {
        var all = new List<T>();
        for (var skip = 0; ; skip += PageSize)
        {
            var separator = path.Contains('?', StringComparison.Ordinal) ? '&' : '?';
            var page = await http.GetFromJsonAsync<List<T>>(
                string.Create(CultureInfo.InvariantCulture, $"{path}{separator}skip={skip}&take={PageSize}"), Json, ct) ?? [];
            all.AddRange(page);
            if (page.Count < PageSize)
            {
                return all;
            }
        }
    }

    private async Task<T> PostAsync<T>(string path, object body, CancellationToken ct)
    {
        using var response = await http.PostAsJsonAsync(path, body, Json, ct);
        if (!response.IsSuccessStatusCode)
        {
            var problem = await response.Content.ReadAsStringAsync(ct);
            throw new HttpRequestException($"POST {path} failed with {(int)response.StatusCode}: {problem}");
        }

        return (await response.Content.ReadFromJsonAsync<T>(Json, ct))!;
    }

    private sealed class Counts
    {
        public int Facilities;
        public int Spots;
        public int Sensors;
    }

    // Minimal copies of the API contracts (the simulator does not reference src/ on purpose).
    private sealed record FacilityDto(Guid Id, string Name, GeoPointDto Location);

    private sealed record GeoPointDto(IReadOnlyList<double> Coordinates);

    private sealed record SpotDto(Guid Id, string Code);

    private sealed record SensorDto(Guid Id, Guid SpotId);

    private sealed record FacilityAvailabilityDto(Guid FacilityId, int Spots);
}
