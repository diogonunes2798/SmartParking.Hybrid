using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SmartParking.Domain.Common;

namespace SmartParking.Infrastructure.Relational.Configurations;

internal static class MappingExtensions
{
    /// <summary>Length of the text columns that store enum names.</summary>
    public const int EnumMaxLength = 30;

    /// <summary>
    /// Common mapping of an aggregate: application-generated UUIDv7 key (no database round trip,
    /// index-friendly ordering) and PostgreSQL's <c>xmin</c> system column as concurrency token.
    /// </summary>
    public static void HasIdAndVersion<T>(this EntityTypeBuilder<T> builder)
        where T : class, IVersioned
    {
        builder.HasKey("Id");
        builder.Property<Guid>("Id").ValueGeneratedNever();

        // Npgsql maps a uint row version to xmin: no extra column, changes on every UPDATE.
        builder.Property(e => e.Version).IsRowVersion();
    }

    /// <summary>Enums stored by name: readable in psql and robust to reordering (schema evolution, Cap. 6.3).</summary>
    public static PropertyBuilder<TEnum> AsText<TEnum>(this PropertyBuilder<TEnum> builder) =>
        builder.HasConversion<string>().HasMaxLength(EnumMaxLength);
}
