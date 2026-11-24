using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace SmartParking.Infrastructure.Relational;

/// <summary>
/// Used by <c>dotnet ef</c> only. Migrations run as the table owner (<c>smartparking</c>), never as the
/// application login, which has no DDL rights (spec 001). The owner connection string comes from the
/// <c>ConnectionStrings__PostgresMigrations</c> environment variable, so the commands in CLAUDE.md work unchanged.
/// </summary>
internal sealed class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<SmartParkingDbContext>
{
    public const string ConnectionStringVariable = "ConnectionStrings__PostgresMigrations";

    public SmartParkingDbContext CreateDbContext(string[] args)
    {
        var connectionString = Environment.GetEnvironmentVariable(ConnectionStringVariable);
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new InvalidOperationException(
                $"Set {ConnectionStringVariable} to the owner connection string (user smartparking) before running dotnet ef.");
        }

        var options = new DbContextOptionsBuilder<SmartParkingDbContext>()
            .UseNpgsql(connectionString, npgsql => npgsql.UseNetTopologySuite())
            .Options;

        return new SmartParkingDbContext(options);
    }
}
