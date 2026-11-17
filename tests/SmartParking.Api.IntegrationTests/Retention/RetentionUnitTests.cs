using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using SmartParking.Application.Ingestion;
using SmartParking.Application.Retention;
using SmartParking.Infrastructure.Documents.OccupancyEvents;
using SmartParking.Infrastructure.Relational.Monolithic;

namespace SmartParking.Api.IntegrationTests.Retention;

/// <summary>Pure parts of the retention (Phase 10, ADR-0013): no container.</summary>
public sealed class RetentionUnitTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 17, 9, 20, 0, TimeSpan.Zero);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public void The_guard_keeps_the_ttl_at_never_without_a_horizon_and_never_lets_it_pass_the_horizon_within_the_margin()
    {
        Assert.Equal(MongoOccupancyEventLog.NeverExpireSeconds, MongoRawEventRetention.ExpireAfterSeconds(null, Now, TimeSpan.FromHours(1)));

        var horizon = Now - TimeSpan.FromDays(30);
        var seconds = MongoRawEventRetention.ExpireAfterSeconds(horizon, Now, TimeSpan.FromHours(1));
        Assert.Equal(0, seconds % 60); // whole minutes: at most one collMod per minute

        // The TTL monitor removes timestamp < t − E. For every t up to now + margin, that limit is at or before the horizon.
        Assert.True(Now + TimeSpan.FromHours(1) - TimeSpan.FromSeconds(seconds) <= horizon);
        // ...and it is not much later than needed (at most one minute of rounding).
        Assert.True(seconds - (Now - horizon + TimeSpan.FromHours(1)).TotalSeconds < 60);
    }

    [Theory]
    [InlineData("occupancy_events_d20261112", "2026-11-12T00:00:00Z", "2026-11-13T00:00:00Z")]
    [InlineData("occupancy_events_y2026m11", "2026-11-01T00:00:00Z", "2026-12-01T00:00:00Z")]
    [InlineData("occupancy_events_y2026m12", "2026-12-01T00:00:00Z", "2027-01-01T00:00:00Z")]
    public void Partition_ranges_come_from_their_names(string name, string from, string to)
    {
        Assert.True(MonolithicRawEventRetention.TryRange(name, out var start, out var end));
        Assert.Equal((DateTimeOffset.Parse(from, System.Globalization.CultureInfo.InvariantCulture),
            DateTimeOffset.Parse(to, System.Globalization.CultureInfo.InvariantCulture)), (start, end));
    }

    [Theory]
    [InlineData("occupancy_events_default")]
    [InlineData("occupancy_events_d2026111")]
    [InlineData("occupancy_events_y2026m13")]
    [InlineData("spot_state")]
    public void Other_tables_are_never_taken_for_a_time_partition(string name) =>
        Assert.False(MonolithicRawEventRetention.TryRange(name, out _, out _));

    [Fact]
    public async Task The_archive_is_gzip_ndjson_with_its_count_and_hash_and_leaves_no_partial_file()
    {
        var folder = Path.Combine(Path.GetTempPath(), "smartparking-archive-" + Guid.NewGuid().ToString("N"));
        try
        {
            var archive = new GzipNdjsonEventArchive(new RetentionOptions { ArchivePath = folder });
            var day = new DateOnly(2026, 11, 12);
            string[] lines = ["{\"a\":1}", "{\"b\":\"é\"}", "{}"];

            var file = await archive.WriteDayAsync(day, lines.ToAsyncEnumerable(), Ct);

            Assert.Equal(3, file.Lines);
            Assert.Equal(Path.Combine(folder, "occupancy-events-2026-11-12.ndjson.gz"), file.Path);
            Assert.Equal(new FileInfo(file.Path).Length, file.Bytes);
            Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(await File.ReadAllBytesAsync(file.Path, Ct))), file.Sha256);
            Assert.Empty(Directory.GetFiles(folder, "*.partial"));
            await using (var gzip = new GZipStream(File.OpenRead(file.Path), CompressionMode.Decompress))
            {
                using var reader = new StreamReader(gzip);
                Assert.Equal(string.Join('\n', lines) + "\n", await reader.ReadToEndAsync(Ct));
            }

            archive.Delete(day);
            Assert.False(File.Exists(file.Path));
        }
        finally
        {
            if (Directory.Exists(folder))
            {
                Directory.Delete(folder, recursive: true);
            }
        }
    }

    [Fact]
    public void An_event_older_than_the_maximum_age_is_refused_and_one_inside_it_is_accepted()
    {
        static JsonElement Event(DateTimeOffset at) => JsonDocument.Parse(
            $$"""
            {"eventId":"{{Guid.NewGuid()}}","sensorId":"{{Guid.NewGuid()}}","spotId":"{{Guid.NewGuid()}}",
             "facilityId":"{{Guid.NewGuid()}}","type":"OCCUPIED","timestamp":"{{at:O}}"}
            """).RootElement;

        var maxAge = TimeSpan.FromDays(7);
        Assert.NotNull(OccupancyEventValidator.Validate(Event(Now - TimeSpan.FromDays(6.9)), Now, TimeSpan.FromMinutes(5), maxAge, out _));
        Assert.Null(OccupancyEventValidator.Validate(Event(Now - TimeSpan.FromDays(7.1)), Now, TimeSpan.FromMinutes(5), maxAge, out var errors));
        Assert.Contains(errors, e => e.Contains("Ingestion:MaxEventAge", StringComparison.Ordinal));
        Assert.Equal(TimeSpan.FromDays(7), new IngestionSettings().MaxEventAge);
    }

    [Fact]
    public void Retention_options_refuse_nonsense()
    {
        new RetentionOptions().Validate();
        Assert.Throws<InvalidOperationException>(() => new RetentionOptions { RawEvents = TimeSpan.FromMinutes(5) }.Validate());
        Assert.Throws<InvalidOperationException>(() => new RetentionOptions { GuardMargin = TimeSpan.Zero }.Validate());
        Assert.Throws<InvalidOperationException>(() => new RetentionOptions { ArchivePath = " " }.Validate());
        Assert.Throws<InvalidOperationException>(() => new MongoEventLogOptions { BlockCompressor = "lz4" }.Validate());
        new MongoEventLogOptions { BlockCompressor = "zstd" }.Validate();
    }
}
