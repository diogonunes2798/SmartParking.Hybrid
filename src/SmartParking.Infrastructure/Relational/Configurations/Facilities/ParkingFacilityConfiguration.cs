using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using NetTopologySuite.Geometries;
using SmartParking.Domain.Common;
using SmartParking.Domain.Facilities;

namespace SmartParking.Infrastructure.Relational.Configurations.Facilities;

internal sealed class ParkingFacilityConfiguration : IEntityTypeConfiguration<ParkingFacility>
{
    /// <summary>WGS 84, the SRID of <c>geography(Point, 4326)</c> (RF-04).</summary>
    public const int Srid = 4326;

    private static readonly ValueConverter<GeoCoordinate, Point> LocationConverter = new(
        coordinate => new Point(coordinate.Longitude, coordinate.Latitude) { SRID = Srid },
        point => new GeoCoordinate(point.Y, point.X));

    public void Configure(EntityTypeBuilder<ParkingFacility> builder)
    {
        builder.ToTable("parking_facilities", table =>
        {
            table.HasCheckConstraint("ck_parking_facilities_latitude", "ST_Y(location::geometry) BETWEEN -90 AND 90");
            table.HasCheckConstraint("ck_parking_facilities_longitude", "ST_X(location::geometry) BETWEEN -180 AND 180");
        });

        builder.HasKey(facility => facility.Id);
        builder.Property(facility => facility.Id).ValueGeneratedNever();
        builder.IgnoreDomainEvents();

        builder.Property(facility => facility.Name).HasMaxLength(ParkingFacility.NameMaxLength);
        builder.Property(facility => facility.Address).HasMaxLength(ParkingFacility.AddressMaxLength);
        builder.Property(facility => facility.TimeZone).HasMaxLength(ParkingFacility.TimeZoneMaxLength);
        builder.Property(facility => facility.Version).IsRowVersion();

        builder.Property(facility => facility.Location)
            .HasConversion(LocationConverter)
            .HasColumnType("geography (point, 4326)");
        builder.HasIndex(facility => facility.Location).HasMethod("gist");

        builder.HasOne<Operator>()
            .WithMany()
            .HasForeignKey(facility => facility.OperatorId)
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(facility => new { facility.OperatorId, facility.Name }).IsUnique();

        builder.OwnsMany(facility => facility.OpeningHours, hours =>
        {
            hours.ToTable("facility_opening_hours", table =>
            {
                table.HasCheckConstraint("ck_facility_opening_hours_day_of_week", "day_of_week BETWEEN 0 AND 6");
                table.HasCheckConstraint("ck_facility_opening_hours_interval", "opens_at < closes_at");
            });

            hours.WithOwner().HasForeignKey("FacilityId");
            hours.Property<Guid>("FacilityId");
            hours.Property(entry => entry.DayOfWeek).HasConversion<short>();
            hours.HasKey("FacilityId", nameof(FacilityOpeningHours.DayOfWeek));
        });

        builder.Navigation(facility => facility.OpeningHours).UsePropertyAccessMode(PropertyAccessMode.Field);
    }
}
