using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SmartParking.Domain.Tariffs;

namespace SmartParking.Infrastructure.Relational.Configurations;

internal sealed class TariffConfiguration : IEntityTypeConfiguration<Tariff>
{
    public void Configure(EntityTypeBuilder<Tariff> builder)
    {
        builder.ToTable("tariffs", table =>
            table.HasCheckConstraint("ck_tariffs_validity", "valid_to IS NULL OR valid_to > valid_from"));
        builder.HasIdAndVersion();

        builder.Property(t => t.Zone).HasMaxLength(Tariff.ZoneMaxLength);

        builder.ComplexProperty(t => t.Validity, validity =>
        {
            validity.Property(v => v.ValidFrom).HasColumnName("valid_from");
            validity.Property(v => v.ValidTo).HasColumnName("valid_to");
        });

        // Calculation rules as a typed jsonb document (ADR-0003): the rule model is still provisional
        // (TODO(Diogo)), and new rule kinds should not require a relational migration.
        // Get-only properties are not mapped by convention; listing them lets EF bind the constructor.
        builder.ComplexProperty(t => t.Rules, rules =>
        {
            rules.ToJson("rules");
            rules.Property(r => r.HourlyRate);
            rules.Property(r => r.FreeMinutes);
            rules.Property(r => r.DailyCap);
        });

        // Lookup of the tariff in force for a zone (UC-02, closing a session).
        builder.HasIndex(t => t.Zone);
    }
}
