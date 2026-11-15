using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SmartParking.Domain.Facilities;

namespace SmartParking.Infrastructure.Relational.Configurations;

internal sealed class ParkingFacilityConfiguration : IEntityTypeConfiguration<ParkingFacility>
{
    public void Configure(EntityTypeBuilder<ParkingFacility> builder)
    {
        var maxLat = GeoPoint.MaxLatitude.ToString(CultureInfo.InvariantCulture);
        var maxLon = GeoPoint.MaxLongitude.ToString(CultureInfo.InvariantCulture);

        builder.ToTable("parking_facilities", table =>
        {
            table.HasCheckConstraint("ck_parking_facilities_capacity", "capacity > 0");

            // Same limits as the domain and as Redis GEO (ADR-0003): the database never holds a point
            // that GEOADD would reject when the availability read model is loaded (Phase 4).
            table.HasCheckConstraint("ck_parking_facilities_latitude", $"latitude BETWEEN -{maxLat} AND {maxLat}");
            table.HasCheckConstraint("ck_parking_facilities_longitude", $"longitude BETWEEN -{maxLon} AND {maxLon}");
        });
        builder.HasIdAndVersion();

        builder.Property(f => f.Name).HasMaxLength(ParkingFacility.NameMaxLength);
        builder.Property(f => f.Operator).HasMaxLength(ParkingFacility.OperatorMaxLength);
        builder.Property(f => f.Zone).HasMaxLength(ParkingFacility.ZoneMaxLength);
        builder.Property(f => f.Type).AsText();

        // Location as two plain double precision columns (ADR-0003), not GeoJSON text nor PostGIS.
        builder.ComplexProperty(f => f.Location, location =>
        {
            location.Property(p => p.Latitude).HasColumnName("latitude");
            location.Property(p => p.Longitude).HasColumnName("longitude");
        });

        // UC-03 and UC-04 filter by zone.
        builder.HasIndex(f => f.Zone);
    }
}

internal sealed class ParkingSpotConfiguration : IEntityTypeConfiguration<ParkingSpot>
{
    public void Configure(EntityTypeBuilder<ParkingSpot> builder)
    {
        builder.ToTable("parking_spots");
        builder.HasIdAndVersion();

        builder.Property(s => s.Code).HasMaxLength(ParkingSpot.CodeMaxLength);
        builder.Property(s => s.AllowedVehicleType).AsText();

        // Restrict: configuration data must never disappear in cascade (deleting a facility with spots fails).
        builder.HasOne<ParkingFacility>().WithMany().HasForeignKey(s => s.FacilityId).OnDelete(DeleteBehavior.Restrict);

        // A code identifies a spot within its facility. Also serves the FK lookup by facility_id (leading column).
        builder.HasIndex(s => new { s.FacilityId, s.Code }).IsUnique();
    }
}

internal sealed class SensorConfiguration : IEntityTypeConfiguration<Sensor>
{
    public void Configure(EntityTypeBuilder<Sensor> builder)
    {
        builder.ToTable("sensors");
        builder.HasIdAndVersion();

        builder.Property(s => s.Model).HasMaxLength(Sensor.ModelMaxLength);
        builder.Property(s => s.FirmwareVersion).HasMaxLength(Sensor.FirmwareVersionMaxLength);
        builder.Property(s => s.Status).AsText();

        builder.HasOne<ParkingSpot>().WithMany().HasForeignKey(s => s.SpotId).OnDelete(DeleteBehavior.Restrict);

        // At most one sensor per spot (decision P4 of Phase 2a); the ingestion maps sensor → spot unambiguously.
        builder.HasIndex(s => s.SpotId).IsUnique();
    }
}
