using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using SmartParking.Infrastructure;
using SmartParking.Tests.Common;
using SmartParking.Workers.Tests.Fixtures;

namespace SmartParking.Workers.Tests.Features.Configuration;

/// <summary>
/// AC-29 (spec 002): the Workers connect as an application login, a member of <c>smartparking_app</c>, so the audit
/// is append-only for them too (RNF-06). Closes the open point of F2 (the Workers connected as the owner).
/// </summary>
[Collection(WorkersCollectionDefinition.Name)]
public sealed class WorkersAppLoginTests(WorkersFixture fixture)
{
    [Fact]
    public async Task UpdateAudit_WithWorkersConnection_Fails42501_AC29()
    {
        // Arrange: the data source the Workers host itself uses.
        var cancellationToken = TestContext.Current.CancellationToken;
        var dataSource = fixture.Expiry.Services.GetRequiredService<NpgsqlDataSource>();
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);

        // Act
        var error = await Assert.ThrowsAsync<PostgresException>(async () =>
        {
            await using var update = new NpgsqlCommand("UPDATE audit_entries SET actor = actor", connection);
            await update.ExecuteNonQueryAsync(cancellationToken);
        });

        // Assert
        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, error.SqlState);
        await using var whoAmI = new NpgsqlCommand(
            "SELECT current_user, r.rolsuper, pg_has_role(current_user, 'smartparking_app', 'MEMBER') FROM pg_roles r WHERE r.rolname = current_user",
            connection);
        await using var reader = await whoAmI.ExecuteReaderAsync(cancellationToken);
        Assert.True(await reader.ReadAsync(cancellationToken));
        Assert.Equal(DatabaseContainers.WorkersLogin, reader.GetString(0));
        Assert.False(reader.GetBoolean(1));
        Assert.True(reader.GetBoolean(2));
    }

    [Fact]
    public void WorkersAppSettings_Postgres_IsApplicationLoginNotOwner_AC29()
    {
        // Arrange
        using var settings = JsonDocument.Parse(File.ReadAllText(FindWorkersAppSettings()));

        // Act
        var connectionString = settings.RootElement.GetProperty("ConnectionStrings").GetProperty("Postgres").GetString();
        var username = new NpgsqlConnectionStringBuilder(connectionString).Username;

        // Assert
        Assert.Equal(DatabaseContainers.WorkersLogin, username);
    }

    [Theory]
    [InlineData("Reservations:ExpiryScanSeconds", "0")]
    [InlineData("Reservations:MaxDurationHours", "0")]
    [InlineData("Reservations:ExpiryGraceMinutes", "-1")]
    public void Startup_ReservationsOptionInvalid_FailsNamingTheKey(string key, string value)
    {
        // Arrange
        var settings = DatabaseContainers.UnreachableSettings(PersistenceMode.Hybrid);
        settings[key] = value;
        using var factory = new ConfiguredFactory<Program>(settings);

        // Act + Assert
        StartupAssertions.AssertStartupFailsNaming(() => factory.CreateClient().Dispose(), key);
    }

    private static string FindWorkersAppSettings()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            var candidate = Path.Combine(directory.FullName, "src", "SmartParking.Workers", "appsettings.json");
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new FileNotFoundException("src/SmartParking.Workers/appsettings.json was not found above the test output directory.");
    }
}
