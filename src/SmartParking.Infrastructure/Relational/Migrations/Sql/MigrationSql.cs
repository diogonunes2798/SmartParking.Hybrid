namespace SmartParking.Infrastructure.Relational.Migrations.Sql;

/// <summary>Versioned SQL scripts embedded in the assembly and run by the migrations.</summary>
internal static class MigrationSql
{
    public const string AppRole = "0001_app_role.sql";

    public static string Read(string fileName)
    {
        using var stream = typeof(MigrationSql).Assembly.GetManifestResourceStream($"Sql.{fileName}")
            ?? throw new InvalidOperationException($"Embedded SQL script {fileName} was not found.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
