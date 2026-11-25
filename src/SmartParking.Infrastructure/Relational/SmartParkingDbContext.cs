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
/// It is the unit of work (D10): one save writes the business change, one <see cref="OutboxMessage"/> per domain
/// event and one <see cref="AuditEntry"/> per entity that event changed, in one transaction (spec 001, spec 002,
/// ADR 0006). Never 2PC.
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
    /// Turns every pending domain event into one outbox message and one audit entry per entity the event changed,
    /// all pointing to that message (ADR 0006). By default the changed entity is the raising root, which is the F2
    /// behaviour. Runs before the save, so every row joins the business change in the same transaction.
    /// </summary>
    private void AppendAuditAndOutbox()
    {
        var now = clock.GetUtcNow();
        var traceId = Activity.Current?.TraceId.ToHexString();

        foreach (var rootEntry in ChangeTracker.Entries<AggregateRoot>().ToList())
        {
            var raisedEvents = rootEntry.Entity.DequeueRaisedEvents();

            // A change confined to owned rows in their own table (for example, only the opening hours) leaves the
            // root Unchanged: EF Core would neither check nor bump its xmin, so a stale ETag could overwrite it and
            // the ETag would not change. Touching the root makes the UPDATE ... WHERE xmin = @original run. Only a
            // root that one of its events declares as changed is touched (ADR 0006).
            if (rootEntry.State == EntityState.Unchanged
                && raisedEvents.Any(raised => raised.Changed.Any(entity => ReferenceEquals(entity, rootEntry.Entity))))
            {
                rootEntry.State = EntityState.Modified;
            }

            foreach (var raised in raisedEvents)
            {
                var message = new OutboxMessage
                {
                    Id = Guid.CreateVersion7(now),
                    Type = OutboxType(raised.Event),
                    Payload = JsonSerializer.Serialize(raised.Event, raised.Event.GetType(), AuditSnapshot.JsonOptions),
                    OccurredAt = now,
                    TraceId = traceId,
                };

                OutboxMessages.Add(message);

                foreach (var entity in raised.Changed)
                {
                    var entry = TrackedEntry(entity, raised.Event);
                    AuditEntries.Add(new AuditEntry
                    {
                        Id = Guid.CreateVersion7(now),
                        OccurredAt = now,
                        Actor = actor.Name,
                        EntityType = entry.Metadata.ClrType.Name,
                        EntityId = KeyOf(entry),
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

    /// <summary>The change tracker entry of a changed entity; the repository must have added or loaded it.</summary>
    private EntityEntry TrackedEntry(object entity, IDomainEvent domainEvent)
    {
        var entry = Entry(entity);
        return entry.State != EntityState.Detached
            ? entry
            : throw new InvalidOperationException(
                $"{entity.GetType().Name}, changed by {domainEvent.GetType().Name}, is not tracked by the unit of work.");
    }

    /// <summary>Every audited entity has a single Guid primary key; non-root entities share no base type.</summary>
    private static Guid KeyOf(EntityEntry entry)
    {
        var key = entry.Metadata.FindPrimaryKey()
            ?? throw new InvalidOperationException($"{entry.Metadata.ClrType.Name} has no primary key.");

        return key.Properties is [var property] && entry.Property(property.Name).CurrentValue is Guid id
            ? id
            : throw new InvalidOperationException($"{entry.Metadata.ClrType.Name} has no single Guid primary key.");
    }
}
