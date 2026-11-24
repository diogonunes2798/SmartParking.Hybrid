using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SmartParking.Domain.Facilities;
using SmartParking.Domain.Sessions;
using SmartParking.Domain.Users;

namespace SmartParking.Infrastructure.Relational.Configurations.Sessions;

/// <summary>
/// Reservations. The <c>EXCLUDE</c> constraint (no overlapping active reservations of one spot)
/// is added by SQL in the <c>Initial</c> migration.
/// </summary>
internal sealed class ReservationConfiguration : IEntityTypeConfiguration<Reservation>
{
    public void Configure(EntityTypeBuilder<Reservation> builder)
    {
        builder.ToTable("reservations", table =>
            table.HasCheckConstraint("ck_reservations_interval", "ends_at > starts_at"));

        builder.HasKey(reservation => reservation.Id);
        builder.Property(reservation => reservation.Id).ValueGeneratedNever();
        builder.IgnoreDomainEvents();
        builder.Property(reservation => reservation.Version).IsRowVersion();

        builder.HasOne<ParkingSpot>().WithMany().HasForeignKey(reservation => reservation.SpotId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<User>().WithMany().HasForeignKey(reservation => reservation.UserId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<Vehicle>().WithMany().HasForeignKey(reservation => reservation.VehicleId).OnDelete(DeleteBehavior.Restrict);
    }
}
