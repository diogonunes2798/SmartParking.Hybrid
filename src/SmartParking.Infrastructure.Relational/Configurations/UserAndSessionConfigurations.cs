using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SmartParking.Domain.Facilities;
using SmartParking.Domain.Sessions;
using SmartParking.Domain.Tariffs;
using SmartParking.Domain.Users;

namespace SmartParking.Infrastructure.Relational.Configurations;

internal sealed class UserConfiguration : IEntityTypeConfiguration<User>
{
    public void Configure(EntityTypeBuilder<User> builder)
    {
        builder.ToTable("users");
        builder.HasIdAndVersion();

        builder.Property(u => u.DisplayName).HasMaxLength(User.DisplayNameMaxLength);
        builder.Property(u => u.Email).HasMaxLength(User.EmailMaxLength);
        builder.HasIndex(u => u.Email).IsUnique();
    }
}

internal sealed class VehicleConfiguration : IEntityTypeConfiguration<Vehicle>
{
    public void Configure(EntityTypeBuilder<Vehicle> builder)
    {
        builder.ToTable("vehicles");
        builder.HasIdAndVersion();

        builder.Property(v => v.LicensePlate).HasMaxLength(Vehicle.LicensePlateMaxLength);
        builder.Property(v => v.Type).AsText();

        builder.HasOne<User>().WithMany().HasForeignKey(v => v.UserId).OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(v => v.UserId);
        builder.HasIndex(v => v.LicensePlate).IsUnique();
    }
}

/// <summary>Relational mapping of sessions: the default of decision D1.</summary>
internal sealed class ParkingSessionConfiguration : IEntityTypeConfiguration<ParkingSession>
{
    public void Configure(EntityTypeBuilder<ParkingSession> builder)
    {
        builder.ToTable("parking_sessions", table =>
        {
            table.HasCheckConstraint("ck_parking_sessions_ended_at", "ended_at IS NULL OR ended_at >= started_at");
            table.HasCheckConstraint("ck_parking_sessions_cost", "cost IS NULL OR cost >= 0");
        });
        builder.HasIdAndVersion();

        builder.Property(s => s.Status).AsText();

        builder.HasOne<ParkingSpot>().WithMany().HasForeignKey(s => s.SpotId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<Vehicle>().WithMany().HasForeignKey(s => s.VehicleId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<User>().WithMany().HasForeignKey(s => s.UserId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<Tariff>().WithMany().HasForeignKey(s => s.TariffId).OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(s => s.VehicleId);
        builder.HasIndex(s => s.UserId);
        builder.HasIndex(s => s.TariffId);

        // History of a spot (UC-04) and FK lookup.
        builder.HasIndex(s => new { s.SpotId, s.StartedAt });

        // Invariant enforced by the database, not only by the use case: a spot has at most one open session,
        // even under concurrent requests (UC-02).
        builder.HasIndex(s => s.SpotId)
            .IsUnique()
            .HasFilter("status = 'Open'")
            .HasDatabaseName("ux_parking_sessions_spot_id_open");
    }
}

internal sealed class PaymentConfiguration : IEntityTypeConfiguration<Payment>
{
    public void Configure(EntityTypeBuilder<Payment> builder)
    {
        builder.ToTable("payments", table => table.HasCheckConstraint("ck_payments_amount", "amount >= 0"));
        builder.HasIdAndVersion();

        builder.Property(p => p.Method).AsText();
        builder.Property(p => p.Status).AsText();

        builder.HasOne<ParkingSession>().WithMany().HasForeignKey(p => p.SessionId).OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(p => p.SessionId);
    }
}
