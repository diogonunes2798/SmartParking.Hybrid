using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SmartParking.Domain.Facilities;

namespace SmartParking.Infrastructure.Relational.Configurations.Facilities;

/// <summary>Spot configuration only: there is no occupancy column (spec 001, AC-3).</summary>
internal sealed class ParkingSpotConfiguration : IEntityTypeConfiguration<ParkingSpot>
{
    public void Configure(EntityTypeBuilder<ParkingSpot> builder)
    {
        builder.ToTable("parking_spots");
        builder.HasKey(spot => spot.Id);
        builder.Property(spot => spot.Id).ValueGeneratedNever();
        builder.IgnoreDomainEvents();

        builder.Property(spot => spot.Code).HasMaxLength(ParkingSpot.CodeMaxLength);
        builder.Property(spot => spot.Version).IsRowVersion();

        builder.HasOne<ParkingFacility>()
            .WithMany()
            .HasForeignKey(spot => spot.FacilityId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<Zone>()
            .WithMany()
            .HasForeignKey(spot => new { spot.FacilityId, spot.ZoneId })
            .HasPrincipalKey(zone => new { zone.FacilityId, zone.Id })
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(spot => new { spot.FacilityId, spot.Code }).IsUnique();

        // Target of the composite FK from sensors: the spot must belong to the sensor's facility.
        builder.HasAlternateKey(spot => new { spot.FacilityId, spot.Id });
    }
}
