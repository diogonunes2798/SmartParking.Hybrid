using Microsoft.EntityFrameworkCore;

namespace SmartParking.Infrastructure.Relational;

/// <summary>
/// Schema operations that run as the table owner, never as the application login (spec 001).
/// Used by the integration tests; in development the same migrations run through <c>dotnet ef database update</c>.
/// </summary>
public static class RelationalDatabase
{
    /// <summary>The group role created by the <c>Initial</c> migration; the Api login is a member.</summary>
    public const string ApplicationRole = "smartparking_app";

    public static async Task MigrateAsync(string ownerConnectionString, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ownerConnectionString);

        await using var context = CreateOwnerContext(ownerConnectionString);
        await context.Database.MigrateAsync(cancellationToken);
    }

    internal static SmartParkingDbContext CreateOwnerContext(string ownerConnectionString) =>
        new(new DbContextOptionsBuilder<SmartParkingDbContext>()
            .UseNpgsql(ownerConnectionString, npgsql => npgsql.UseNetTopologySuite())
            .Options);
}
