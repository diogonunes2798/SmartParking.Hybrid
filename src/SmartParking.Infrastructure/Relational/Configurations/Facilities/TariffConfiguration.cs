using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SmartParking.Domain.Facilities;

namespace SmartParking.Infrastructure.Relational.Configurations.Facilities;

/// <summary>
/// Tariff versions. The <c>EXCLUDE</c> constraint (one valid version per facility at any instant)
/// cannot be modelled in EF Core and is added by SQL in the <c>Initial</c> migration.
/// </summary>
internal sealed class TariffConfiguration : IEntityTypeConfiguration<Tariff>
{
    public void Configure(EntityTypeBuilder<Tariff> builder)
    {
        builder.ToTable("tariffs", table =>
        {
            table.HasCheckConstraint("ck_tariffs_validity", "valid_to IS NULL OR valid_to > valid_from");
            table.HasCheckConstraint("ck_tariffs_price_per_hour", "price_per_hour >= 0");
            table.HasCheckConstraint(
                "ck_tariffs_billing_increment_minutes",
                $"billing_increment_minutes BETWEEN {Tariff.MinBillingIncrementMinutes} AND {Tariff.MaxBillingIncrementMinutes}");
            table.HasCheckConstraint("ck_tariffs_daily_maximum", "daily_maximum IS NULL OR daily_maximum >= 0");
            table.HasCheckConstraint("ck_tariffs_free_minutes", "free_minutes >= 0");
            table.HasCheckConstraint("ck_tariffs_currency", "currency ~ '^[A-Z]{3}$'");
        });

        builder.HasKey(tariff => tariff.Id);
        builder.Property(tariff => tariff.Id).ValueGeneratedNever();
        builder.IgnoreDomainEvents();

        builder.Property(tariff => tariff.PricePerHour).HasPrecision(10, 2);
        builder.Property(tariff => tariff.DailyMaximum).HasPrecision(10, 2);
        builder.Property(tariff => tariff.Currency).HasMaxLength(Tariff.CurrencyLength).IsFixedLength();
        builder.Property(tariff => tariff.Version).IsRowVersion();

        builder.HasOne<ParkingFacility>()
            .WithMany()
            .HasForeignKey(tariff => tariff.FacilityId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
