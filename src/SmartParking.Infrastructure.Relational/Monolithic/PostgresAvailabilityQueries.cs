using Npgsql;
using NpgsqlTypes;
using SmartParking.Application.Availability;
using SmartParking.Domain.Facilities;

namespace SmartParking.Infrastructure.Relational.Monolithic;

/// <summary>
/// UC-03 in the monolithic variant (ADR-0012): availability computed by one query, without Redis. Same answer shape and
/// rules as the Redis read model (ADR-0008), so the HTTP contract does not change:
/// <list type="bullet">
/// <item>facilities within the radius, nearest first, at most <see cref="AvailabilitySearch.Limit"/>: PostGIS
/// <c>ST_DWithin</c> on <c>geography</c>, answered by the GiST expression index <c>ix_parking_facilities_geography</c>,
/// like <c>GEOSEARCH … ASC COUNT n</c>;</item>
/// <item>distance on a sphere (<c>use_spheroid = false</c>), as Redis GEO does. The radii differ (PostGIS 6 371 008.8 m,
/// Redis 6 372 797.6 m): the distances of the two variants differ by about 0.03 %;</item>
/// <item>a spot with no event yet, or whose last event is FREED (<c>type = 2</c>, <see cref="MonolithicSchema.Freed"/>), counts as free (a new spot starts FREE, ADR-0008);</item>
/// <item>with a vehicle type, the counts are of that type, and a facility without such spots is left out — after the
/// limit, as Redis does.</item>
/// </list>
/// The counts are strongly consistent: they include every event the API has answered 202 for.
/// </summary>
public sealed class PostgresAvailabilityQueries(NpgsqlDataSource dataSource) : IAvailabilityQueries
{
    /// <summary>The query (public for the plan test: it must use the GiST index, not a scan of every facility).</summary>
    public const string SearchSql =
        $"""
        WITH origin AS (
            SELECT postgis.ST_SetSRID(postgis.ST_MakePoint(@lon, @lat), 4326)::postgis.geography AS g
        ), near AS (
            SELECT f.id, f.name, f.zone, f.type, f.latitude, f.longitude, f.capacity,
                   postgis.ST_Distance({MonolithicSchema.FacilityGeography}, o.g, false) AS distance
            FROM parking_facilities f, origin o
            WHERE postgis.ST_DWithin({MonolithicSchema.FacilityGeography}, o.g, @radius, false)
            ORDER BY distance, f.id
            LIMIT @limit
        )
        SELECT n.id, n.name, n.zone, n.type, n.latitude, n.longitude, n.capacity, n.distance,
               count(s.id)::int AS spots,
               (count(s.id) FILTER (WHERE st.type IS NULL OR st.type = 2))::int AS free
        FROM near n
        LEFT JOIN parking_spots s
               ON s.facility_id = n.id AND (@vehicle_type::text IS NULL OR s.allowed_vehicle_type = @vehicle_type::text)
        LEFT JOIN {MonolithicSchema.SpotState} st ON st.spot_id = s.id
        GROUP BY n.id, n.name, n.zone, n.type, n.latitude, n.longitude, n.capacity, n.distance
        ORDER BY n.distance, n.id
        """;

    public async Task<IReadOnlyList<FacilityAvailability>> SearchAsync(AvailabilitySearch search, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(search);
        var command = dataSource.CreateCommand(SearchSql);
        await using var commandScope = command.ConfigureAwait(false);
        AddParameters(command, search);

        var result = new List<FacilityAvailability>();
        var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        await using var readerScope = reader.ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var spots = reader.GetInt32(8);
            if (search.VehicleType is not null && spots == 0)
            {
                continue; // no spot of the requested type in this facility
            }

            result.Add(new FacilityAvailability(
                reader.GetGuid(0),
                reader.GetString(1),
                reader.GetString(2),
                Enum.Parse<FacilityType>(reader.GetString(3)),
                reader.GetDouble(4),
                reader.GetDouble(5),
                reader.GetInt32(6),
                reader.GetDouble(7),
                spots,
                reader.GetInt32(9)));
        }

        return result;
    }

    /// <summary>Binds the parameters of <see cref="SearchSql"/> (shared with the plan test).</summary>
    public static void AddParameters(NpgsqlCommand command, AvailabilitySearch search)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(search);
        command.Parameters.Add(new NpgsqlParameter<double>("lon", NpgsqlDbType.Double) { TypedValue = search.Longitude });
        command.Parameters.Add(new NpgsqlParameter<double>("lat", NpgsqlDbType.Double) { TypedValue = search.Latitude });
        command.Parameters.Add(new NpgsqlParameter<double>("radius", NpgsqlDbType.Double) { TypedValue = search.RadiusMeters });
        command.Parameters.Add(new NpgsqlParameter<int>("limit", NpgsqlDbType.Integer) { TypedValue = search.Limit });
        command.Parameters.Add(new NpgsqlParameter("vehicle_type", NpgsqlDbType.Text)
        {
            Value = search.VehicleType?.ToString() ?? (object)DBNull.Value,
        });
    }
}
