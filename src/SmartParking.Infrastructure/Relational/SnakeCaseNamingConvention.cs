using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;

namespace SmartParking.Infrastructure.Relational;

/// <summary>
/// Store names in <c>snake_case</c> (spec 001) without the EFCore.NamingConventions package.
/// Runs at the end of <c>OnModelCreating</c>, after every configuration. It also adds a <c>CHECK</c>
/// with the allowed values to every enum column stored as text.
/// </summary>
internal static class SnakeCaseNamingConvention
{
    public static void Apply(ModelBuilder modelBuilder)
    {
        foreach (var entity in modelBuilder.Model.GetEntityTypes())
        {
            if (entity.GetTableName() is { } table)
            {
                entity.SetTableName(ToSnakeCase(table));
            }

            foreach (var property in entity.GetProperties().Where(property => !IsXminVersion(property)))
            {
                property.SetColumnName(ToSnakeCase(property.GetColumnName()));
            }

            foreach (var key in entity.GetKeys())
            {
                key.SetName(ToSnakeCase(key.GetName()!));
            }

            foreach (var foreignKey in entity.GetForeignKeys())
            {
                foreignKey.SetConstraintName(ToSnakeCase(foreignKey.GetConstraintName()!));
            }

            foreach (var index in entity.GetIndexes())
            {
                index.SetDatabaseName(ToSnakeCase(index.GetDatabaseName()!));
            }

            AddEnumChecks(entity);
        }
    }

    public static string ToSnakeCase(string name)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);

        var builder = new StringBuilder(name.Length + 8);
        for (var i = 0; i < name.Length; i++)
        {
            var current = name[i];
            if (char.IsUpper(current))
            {
                if (i > 0 && NeedsSeparator(name, i))
                {
                    builder.Append('_');
                }

                builder.Append(char.ToLowerInvariant(current));
            }
            else
            {
                builder.Append(current);
            }
        }

        return builder.ToString();
    }

    /// <summary>A word starts at an upper-case letter after a lower-case letter or digit, or at the last capital of an acronym.</summary>
    private static bool NeedsSeparator(string name, int i)
    {
        var previous = name[i - 1];
        if (previous == '_')
        {
            return false;
        }

        var nextIsLower = i + 1 < name.Length && char.IsLower(name[i + 1]);
        return char.IsLower(previous) || char.IsDigit(previous) || (char.IsUpper(previous) && nextIsLower);
    }

    /// <summary>
    /// A <c>uint</c> row version is the PostgreSQL <c>xmin</c> system column. Npgsql names it only when the
    /// column name is not set explicitly, so renaming it here would create a real <c>version</c> column.
    /// </summary>
    private static bool IsXminVersion(IMutableProperty property) =>
        property.ClrType == typeof(uint) && property.IsConcurrencyToken && property.ValueGenerated == ValueGenerated.OnAddOrUpdate;

    private static void AddEnumChecks(IMutableEntityType entity)
    {
        if (entity.GetTableName() is not { } table)
        {
            return;
        }

        foreach (var property in entity.GetProperties())
        {
            var clrType = Nullable.GetUnderlyingType(property.ClrType) ?? property.ClrType;
            if (!clrType.IsEnum || property.GetProviderClrType() != typeof(string))
            {
                continue;
            }

            var column = property.GetColumnName();
            var values = string.Join(", ", Enum.GetNames(clrType).Select(value => $"'{value}'"));
            entity.AddCheckConstraint($"ck_{table}_{column}", $"{column} IN ({values})");
        }
    }
}
