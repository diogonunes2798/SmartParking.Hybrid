using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SmartParking.Domain.Users;

namespace SmartParking.Infrastructure.Relational.Configurations.Users;

internal sealed class VehicleConfiguration : IEntityTypeConfiguration<Vehicle>
{
    public void Configure(EntityTypeBuilder<Vehicle> builder)
    {
        builder.ToTable("vehicles", table =>
            table.HasCheckConstraint("ck_vehicles_license_plate_normalised", "license_plate ~ '^[A-Z0-9]+$'"));

        builder.HasKey(vehicle => vehicle.Id);
        builder.Property(vehicle => vehicle.Id).ValueGeneratedNever();

        builder.Property(vehicle => vehicle.LicensePlate).HasMaxLength(Vehicle.LicensePlateMaxLength);
        builder.HasIndex(vehicle => vehicle.LicensePlate).IsUnique();

        builder.HasOne<User>()
            .WithMany()
            .HasForeignKey(vehicle => vehicle.UserId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
