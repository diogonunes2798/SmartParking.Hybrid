using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using SmartParking.Application.Abstractions;
using SmartParking.Application.Abstractions.Persistence;
using SmartParking.Domain.Common;
using SmartParking.Domain.Facilities;
using SmartParking.Domain.Sessions;
using SmartParking.Domain.Users;
using SmartParking.Infrastructure.Relational.Auditing;
using SmartParking.Infrastructure.Relational.Outbox;

namespace SmartParking.Infrastructure.Relational;

/// <summary>
/// Relational sublayer (PostgreSQL + PostGIS): the three aggregates of Table 15, the audit trail and the outbox.
/// It is the unit of work (D10): one save writes the business change, one <see cref="AuditEntry"/> and one
/// <see cref="OutboxMessage"/> per domain event, in one transaction (spec 001). Never 2PC.
/// </summary>
internal sealed class SmartParkingDbContext(
    DbContextOptions<SmartParkingDbContext> options,
    ICurrentActor actor,
    TimeProvider clock) : DbContext(options), IUnitOfWork
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

    async Task<Result> IUnitOfWork.SaveChangesAsync(CancellationToken cancellationToken)
    {
        AppendAuditAndOutbox();

        try
        {
            await SaveChangesAsync(cancellationToken);
            return Result.Success();
        }
        catch (DbUpdateConcurrencyException)
        {
            // The transaction was rolled back; drop the pending changes and the appended audit/outbox rows,
            // so a later save in the same scope cannot write them as orphans.
            ChangeTracker.Clear();
            return ConcurrencyCheck.StaleVersion;
        }
        catch (DbUpdateException exception) when (PostgresErrorTranslator.TryTranslate(exception, out var error))
        {
            ChangeTracker.Clear();
            return error;
        }
    }

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

    private static string OutboxType(IDomainEvent domainEvent) =>
        domainEvent.GetType().GetCustomAttribute<DomainEventAttribute>()?.TypeName
        ?? throw new InvalidOperationException($"{domainEvent.GetType().Name} has no [DomainEvent] attribute.");

    private static AuditOperation OperationOf(EntityEntry entry) => entry.State switch
    {
        EntityState.Added => AuditOperation.Created,
        EntityState.Deleted => AuditOperation.Deleted,
        _ => AuditOperation.Updated,
    };

    /// <summary>
    /// Turns every pending domain event into an outbox message and an audit entry that points to it.
    /// Runs before the save, so both rows join the business change in the same transaction.
    /// </summary>
    private void AppendAuditAndOutbox()
    {
        var now = clock.GetUtcNow();
        var traceId = Activity.Current?.TraceId.ToHexString();

        foreach (var entry in ChangeTracker.Entries<AggregateRoot>().ToList())
        {
            // A change confined to owned rows in their own table (for example, only the opening hours) leaves the
            // root Unchanged: EF Core would neither check nor bump its xmin, so a stale ETag could overwrite it and
            // the ETag would not change. Touching the root makes the UPDATE ... WHERE xmin = @original run.
            if (entry.State == EntityState.Unchanged && entry.Entity.DomainEvents.Count > 0)
            {
                entry.State = EntityState.Modified;
            }

            foreach (var domainEvent in entry.Entity.DequeueDomainEvents())
            {
                var message = new OutboxMessage
                {
                    Id = Guid.CreateVersion7(now),
                    Type = OutboxType(domainEvent),
                    Payload = JsonSerializer.Serialize(domainEvent, domainEvent.GetType(), AuditSnapshot.JsonOptions),
                    OccurredAt = now,
                    TraceId = traceId,
                };

                OutboxMessages.Add(message);
                AuditEntries.Add(new AuditEntry
                {
                    Id = Guid.CreateVersion7(now),
                    OccurredAt = now,
                    Actor = actor.Name,
                    EntityType = entry.Metadata.ClrType.Name,
                    EntityId = entry.Entity.Id,
                    Operation = OperationOf(entry),
                    Before = AuditSnapshot.Before(entry),
                    After = AuditSnapshot.After(entry),
                    TraceId = traceId,
                    OutboxMessageId = message.Id,
                });
            }
        }
    }
}
