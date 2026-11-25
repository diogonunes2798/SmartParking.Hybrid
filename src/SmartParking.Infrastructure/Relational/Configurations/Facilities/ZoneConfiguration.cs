using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SmartParking.Domain.Facilities;

namespace SmartParking.Infrastructure.Relational.Configurations.Facilities;

internal sealed class ZoneConfiguration : IEntityTypeConfiguration<Zone>
{
    public void Configure(EntityTypeBuilder<Zone> builder)
    {
        builder.ToTable("zones");
        builder.HasKey(zone => zone.Id);
        builder.Property(zone => zone.Id).ValueGeneratedNever();
        builder.IgnoreDomainEvents();

        builder.Property(zone => zone.Name).HasMaxLength(Zone.NameMaxLength);
        builder.Property(zone => zone.Version).IsRowVersion();

        builder.HasOne<ParkingFacility>()
            .WithMany()
            .HasForeignKey(zone => zone.FacilityId)
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(zone => new { zone.FacilityId, zone.Name }).IsUnique();

        // Target of the composite FKs from spots and sensors: the zone must belong to the same facility.
        builder.HasAlternateKey(zone => new { zone.FacilityId, zone.Id });
    }
}
