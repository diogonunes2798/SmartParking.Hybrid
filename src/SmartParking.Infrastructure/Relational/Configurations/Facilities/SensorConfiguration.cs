using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SmartParking.Domain.Facilities;

namespace SmartParking.Infrastructure.Relational.Configurations.Facilities;

internal sealed class SensorConfiguration : IEntityTypeConfiguration<Sensor>
{
    public void Configure(EntityTypeBuilder<Sensor> builder)
    {
        builder.ToTable("sensors", table =>
            table.HasCheckConstraint("ck_sensors_spot_only_for_occupancy", "kind = 'Occupancy' OR spot_id IS NULL"));

        builder.HasKey(sensor => sensor.Id);
        builder.Property(sensor => sensor.Id).ValueGeneratedNever();
        builder.IgnoreDomainEvents();

        builder.Property(sensor => sensor.HardwareId).HasMaxLength(Sensor.HardwareIdMaxLength);
        builder.Property(sensor => sensor.Model).HasMaxLength(Sensor.ModelMaxLength);
        builder.Property(sensor => sensor.Version).IsRowVersion();

        builder.HasOne<ParkingFacility>()
            .WithMany()
            .HasForeignKey(sensor => sensor.FacilityId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<ParkingSpot>()
            .WithMany()
            .HasForeignKey(sensor => new { sensor.FacilityId, sensor.SpotId })
            .HasPrincipalKey(spot => new { spot.FacilityId, spot.Id })
            .IsRequired(false)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<Zone>()
            .WithMany()
            .HasForeignKey(sensor => new { sensor.FacilityId, sensor.ZoneId })
            .HasPrincipalKey(zone => new { zone.FacilityId, zone.Id })
            .IsRequired(false)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(sensor => sensor.HardwareId).IsUnique();
        builder.HasIndex(sensor => sensor.SpotId).IsUnique().HasFilter("spot_id IS NOT NULL");
    }
}
