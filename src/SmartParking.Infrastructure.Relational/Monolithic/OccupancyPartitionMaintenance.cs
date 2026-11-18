using Npgsql;

namespace SmartParking.Infrastructure.Relational.Monolithic;

/// <summary>
/// Keeps the monthly partitions of <c>monolith.occupancy_events</c> ready ahead of time (ADR-0012), so that new events
/// never fall into the DEFAULT partition. A partition can no longer be created for a range that already has rows in
/// DEFAULT; the SQL function skips such a month with a WARNING instead of failing the whole run.
/// Declarative partitioning has no built-in automatic creation (pg_partman is not in the image): this is the usual
/// substitute, called by the Workers at start and once a day.
/// </summary>
public sealed class OccupancyPartitionMaintenance(NpgsqlDataSource dataSource, MonolithicOptions options, TimeProvider time)
{
    /// <summary>
    /// Creates the partitions from the current UTC month to <see cref="MonolithicOptions.PartitionMonthsAhead"/> months ahead
    /// (or the daily ones, with <see cref="PartitionInterval.Day"/>); returns how many were created.
    /// </summary>
    public async Task<int> EnsureAsync(CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow().UtcDateTime;
        if (options.PartitionInterval == PartitionInterval.Day)
        {
            // Daily partitions (Phase 10, ADR-0013): from PartitionDaysBehind days back to PartitionDaysAhead days ahead.
            // Never behind the retention purge horizon: those days were dropped and must not come back as empty partitions
            // (review of Phase 10, finding 8).
            var horizonCommand = dataSource.CreateCommand(
                "SELECT (position AT TIME ZONE 'UTC')::date FROM analytics.retention_positions WHERE name = 'purge_horizon'");
            await using var horizonScope = horizonCommand.ConfigureAwait(false);
            var horizonDay = await horizonCommand.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) switch
            {
                DateOnly d => d,
                DateTime dt => DateOnly.FromDateTime(dt), // Npgsql returns date as DateTime from ExecuteScalar
                _ => (DateOnly?)null,
            };
            var today = DateOnly.FromDateTime(now);
            var fromDay = today.AddDays(-options.PartitionDaysBehind);
            if (horizonDay is { } h && h > fromDay)
            {
                fromDay = h > today ? today : h;
            }

            var days = dataSource.CreateCommand($"SELECT {MonolithicSchema.EnsureDayPartitionsFunction}(@from_day, @days)");
            await using var daysScope = days.ConfigureAwait(false);
            days.Parameters.AddWithValue("from_day", fromDay);
            days.Parameters.AddWithValue("days", today.DayNumber - fromDay.DayNumber + options.PartitionDaysAhead + 1);
            return (int)(await days.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!;
        }

        var command = dataSource.CreateCommand($"SELECT {MonolithicSchema.EnsurePartitionsFunction}(@from_month, @months)");
        await using var commandScope = command.ConfigureAwait(false);
        command.Parameters.AddWithValue("from_month", new DateOnly(now.Year, now.Month, 1));
        command.Parameters.AddWithValue("months", options.PartitionMonthsAhead + 1);
        return (int)(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!;
    }

    /// <summary>
    /// Rows in the DEFAULT partition (review of Phase 8, migrations finding 3). It should stay 0: a month with rows there can
    /// never get its own partition, and every partition creation scans DEFAULT under an ACCESS EXCLUSIVE lock.
    /// </summary>
    public async Task<long> CountDefaultPartitionRowsAsync(CancellationToken cancellationToken)
    {
        var command = dataSource.CreateCommand($"SELECT count(*) FROM {MonolithicSchema.DefaultPartition}");
        await using var commandScope = command.ConfigureAwait(false);
        return (long)(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!;
    }
}
