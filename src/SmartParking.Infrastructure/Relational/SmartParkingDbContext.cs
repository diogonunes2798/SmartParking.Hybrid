using Microsoft.EntityFrameworkCore;
using SmartParking.Domain.Facilities;
using SmartParking.Domain.Sessions;
using SmartParking.Domain.Users;
using SmartParking.Infrastructure.Relational.Auditing;
using SmartParking.Infrastructure.Relational.Outbox;

namespace SmartParking.Infrastructure.Relational;

/// <summary>
/// Relational sublayer (PostgreSQL + PostGIS): the three aggregates of Table 15, the audit trail and the outbox.
/// The schema is created by the <c>Initial</c> migration, run by the table owner (spec 001).
/// </summary>
internal sealed class SmartParkingDbContext(DbContextOptions<SmartParkingDbContext> options) : DbContext(options)
{
    public DbSet<Operator> Operators => Set<Operator>();

    public DbSet<ParkingFacility> ParkingFacilities => Set<ParkingFacility>();

    public DbSet<Zone> Zones => Set<Zone>();

    public DbSet<ParkingSpot> ParkingSpots => Set<ParkingSpot>();

    public DbSet<Sensor> Sensors => Set<Sensor>();

    public DbSet<Tariff> Tariffs => Set<Tariff>();

    public DbSet<User> Users => Set<User>();

    public DbSet<Vehicle> Vehicles => Set<Vehicle>();

    public DbSet<PaymentMethod> PaymentMethods => Set<PaymentMethod>();

    public DbSet<Reservation> Reservations => Set<Reservation>();

    public DbSet<ParkingSession> ParkingSessions => Set<ParkingSession>();

    public DbSet<Payment> Payments => Set<Payment>();

    public DbSet<AuditEntry> AuditEntries => Set<AuditEntry>();

    public DbSet<OutboxMessage> OutboxMessages => Set<OutboxMessage>();

    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        // Enums as text: readable rows and CHECKs that name the values ('Occupancy', 'Active').
        configurationBuilder.Properties<Enum>().HaveConversion<string>().HaveMaxLength(32);
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasPostgresExtension("postgis");
        modelBuilder.HasPostgresExtension("btree_gist");

        modelBuilder.ApplyConfigurationsFromAssembly(
            typeof(SmartParkingDbContext).Assembly,
            type => type.Namespace?.StartsWith(typeof(SmartParkingDbContext).Namespace!, StringComparison.Ordinal) == true);

        SnakeCaseNamingConvention.Apply(modelBuilder);
    }
}
