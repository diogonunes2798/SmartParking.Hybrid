using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SmartParking.Domain.Facilities;
using SmartParking.Domain.Sessions;
using SmartParking.Domain.Users;

namespace SmartParking.Infrastructure.Relational.Configurations.Sessions;

internal sealed class ParkingSessionConfiguration : IEntityTypeConfiguration<ParkingSession>
{
    public void Configure(EntityTypeBuilder<ParkingSession> builder)
    {
        builder.ToTable("parking_sessions", table =>
        {
            table.HasCheckConstraint("ck_parking_sessions_interval", "ended_at IS NULL OR ended_at >= started_at");
            table.HasCheckConstraint("ck_parking_sessions_amount", "amount IS NULL OR amount >= 0");
        });

        builder.HasKey(session => session.Id);
        builder.Property(session => session.Id).ValueGeneratedNever();
        builder.IgnoreDomainEvents();
        builder.Property(session => session.Amount).HasPrecision(10, 2);
        builder.Property(session => session.Version).IsRowVersion();

        builder.HasOne<ParkingSpot>().WithMany().HasForeignKey(session => session.SpotId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<Vehicle>().WithMany().HasForeignKey(session => session.VehicleId).OnDelete(DeleteBehavior.Restrict);

        // The FK index of the Initial migration, declared explicitly so the named index below cannot replace it
        // (the OpenSessionPerVehicle migration only adds; spec 002, AC-1).
        builder.HasIndex(session => session.VehicleId);

        // At most one open session per vehicle (spec 002, AC-7). No uniqueness per spot.
        builder.HasIndex(session => session.VehicleId, "ux_parking_sessions_vehicle_open")
            .IsUnique()
            .HasFilter("status = 'Open'");

        // A tariff version referenced by sessions can never be deleted (spec 001, AC-19).
        builder.HasOne<Tariff>().WithMany().HasForeignKey(session => session.TariffId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<Reservation>()
            .WithMany()
            .HasForeignKey(session => session.ReservationId)
            .IsRequired(false)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
