using Microsoft.EntityFrameworkCore;
using SmartParking.Domain.Facilities;
using SmartParking.Domain.Sessions;
using SmartParking.Domain.Tariffs;
using SmartParking.Domain.Users;
using SmartParking.Infrastructure.Relational.Auditing;

namespace SmartParking.Infrastructure.Relational;

/// <summary>
/// EF Core context of the relational sub-layer (spec §3: configuration, users, sessions, payments, audit).
/// Mappings live in <c>Configurations/</c>, one class per entity, so the domain stays free of EF attributes.
/// </summary>
public sealed class SmartParkingDbContext(DbContextOptions<SmartParkingDbContext> options) : DbContext(options)
{
    public DbSet<ParkingFacility> Facilities => Set<ParkingFacility>();

    public DbSet<ParkingSpot> Spots => Set<ParkingSpot>();

    public DbSet<Sensor> Sensors => Set<Sensor>();

    public DbSet<Tariff> Tariffs => Set<Tariff>();

    public DbSet<User> Users => Set<User>();

    public DbSet<Vehicle> Vehicles => Set<Vehicle>();

    public DbSet<ParkingSession> Sessions => Set<ParkingSession>();

    public DbSet<Payment> Payments => Set<Payment>();

    public DbSet<AuditEntry> AuditLog => Set<AuditEntry>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(SmartParkingDbContext).Assembly);
    }

    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        ArgumentNullException.ThrowIfNull(configurationBuilder);

        // Monetary amounts in EUR, two decimal places.
        configurationBuilder.Properties<decimal>().HavePrecision(10, 2);
    }
}
