using Npgsql;
using SmartParking.Application.Abstractions;
using SmartParking.Infrastructure.Relational;

namespace SmartParking.UnitTests.Infrastructure;

public sealed class PostgresErrorTranslatorTests
{
    [Theory]
    [InlineData(PostgresErrorCodes.UniqueViolation, ErrorType.Conflict)]
    [InlineData(PostgresErrorCodes.ForeignKeyViolation, ErrorType.Conflict)]
    [InlineData(PostgresErrorCodes.ExclusionViolation, ErrorType.Conflict)]
    [InlineData(PostgresErrorCodes.CheckViolation, ErrorType.Validation)]
    public void Translate_ConstraintViolation_MapsToErrorTypeNamingTheConstraint(string sqlState, ErrorType expected)
    {
        // Arrange
        var exception = Violation(sqlState, "parking_spots", "some_constraint");

        // Act
        var error = PostgresErrorTranslator.Translate(exception);

        // Assert
        Assert.NotNull(error);
        Assert.Equal(expected, error.Type);
        Assert.Contains("some_constraint", error.Description, StringComparison.Ordinal);
    }

    [Fact]
    public void Translate_CheckViolation_KeysTheFieldErrorByConstraint()
    {
        // Arrange
        var exception = Violation(PostgresErrorCodes.CheckViolation, "tariffs", "ck_tariffs_free_minutes");

        // Act
        var error = PostgresErrorTranslator.Translate(exception);

        // Assert
        Assert.NotNull(error);
        Assert.True(error.ValidationErrors.ContainsKey("ck_tariffs_free_minutes"));
    }

    [Fact]
    public void Translate_ForeignKeyOnDelete_NamesTheDependentTable()
    {
        // Arrange
        var exception = Violation(PostgresErrorCodes.ForeignKeyViolation, "parking_spots", "fk_parking_spots_parking_facilities_facility_id");
        exception.BatchCommand = new NpgsqlBatchCommand("DELETE FROM parking_facilities WHERE id = @p0 AND xmin = @p1;");

        // Act
        var error = PostgresErrorTranslator.Translate(exception);

        // Assert
        Assert.NotNull(error);
        Assert.Contains("still referenced by rows in parking_spots", error.Description, StringComparison.Ordinal);
    }

    [Fact]
    public void Translate_ForeignKeyOnInsert_SaysTheReferencedRowIsMissing()
    {
        // Arrange
        var exception = Violation(PostgresErrorCodes.ForeignKeyViolation, "parking_facilities", "fk_parking_facilities_operators_operator_id");
        exception.BatchCommand = new NpgsqlBatchCommand("INSERT INTO parking_facilities (id) VALUES (@p0);");

        // Act
        var error = PostgresErrorTranslator.Translate(exception);

        // Assert
        Assert.NotNull(error);
        Assert.Contains("does not exist", error.Description, StringComparison.Ordinal);
    }

    [Fact]
    public void Translate_UnrelatedSqlState_ReturnsNull()
    {
        // Arrange
        var exception = Violation(PostgresErrorCodes.InsufficientPrivilege, "audit_entries", null);

        // Act
        var error = PostgresErrorTranslator.Translate(exception);

        // Assert
        Assert.Null(error);
    }

    [Fact]
    public void Translate_DetailWithRowValues_IsNeverCopied()
    {
        // Arrange
        var exception = new PostgresException(
            "duplicate key value violates unique constraint",
            "ERROR",
            "ERROR",
            PostgresErrorCodes.UniqueViolation,
            detail: "Key (email)=(ana@example.pt) already exists.",
            tableName: "users",
            constraintName: "ix_users_email");

        // Act
        var error = PostgresErrorTranslator.Translate(exception);

        // Assert
        Assert.NotNull(error);
        Assert.DoesNotContain("ana@example.pt", error.Description, StringComparison.Ordinal);
    }

    private static PostgresException Violation(string sqlState, string table, string? constraint) =>
        new("constraint violated", "ERROR", "ERROR", sqlState, tableName: table, constraintName: constraint);
}
