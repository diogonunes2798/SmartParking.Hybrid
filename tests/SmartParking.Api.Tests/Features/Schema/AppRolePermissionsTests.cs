using Npgsql;
using SmartParking.Api.Tests.Fixtures;

namespace SmartParking.Api.Tests.Features.Schema;

/// <summary>The audit trail is append-only for the application login (spec 001, AC-10; RNF-06).</summary>
[Collection(ApiCollectionDefinition.Name)]
public sealed class AppRolePermissionsTests(ApiFixture fixture)
{
    private const string InsertAudit =
        """
        INSERT INTO audit_entries (id, occurred_at, actor, entity_type, entity_id, operation, outbox_message_id)
        VALUES ($1, now(), 'anonymous', 'PermissionProbe', $1, 'Created', $1)
        """;

    [Fact]
    public async Task AppLogin_Inspected_IsNeitherSuperuserNorTableOwner_AC10()
    {
        // Arrange
        var cancellationToken = TestContext.Current.CancellationToken;
        var app = fixture.Databases.AppConnectionString;

        // Act
        var isSuperuser = await DbProbe.ScalarAsync<bool>(
            app, "SELECT rolsuper FROM pg_roles WHERE rolname = current_user", cancellationToken);
        var ownsAudit = await DbProbe.ScalarAsync<bool>(
            app, "SELECT tableowner = current_user FROM pg_tables WHERE tablename = 'audit_entries'", cancellationToken);

        // Assert
        Assert.False(isSuperuser);
        Assert.False(ownsAudit);
    }

    [Fact]
    public async Task InsertSelectAudit_AsAppUser_Succeed_AC10()
    {
        // Arrange
        var cancellationToken = TestContext.Current.CancellationToken;
        var app = fixture.Databases.AppConnectionString;
        var id = Guid.CreateVersion7();

        // Act
        var inserted = await DbProbe.ExecuteAsync(app, InsertAudit, cancellationToken, id);
        var found = await DbProbe.ScalarAsync<long>(app, "SELECT count(*) FROM audit_entries WHERE id = $1", cancellationToken, id);

        // Assert
        Assert.Equal(1, inserted);
        Assert.Equal(1, found);
    }

    [Theory]
    [InlineData("UPDATE audit_entries SET actor = 'tampered' WHERE id = $1")]
    [InlineData("DELETE FROM audit_entries WHERE id = $1")]
    [InlineData("TRUNCATE audit_entries")]
    public async Task UpdateDeleteTruncateAudit_AsAppUser_Fail42501_AC10(string statement)
    {
        // Arrange
        var cancellationToken = TestContext.Current.CancellationToken;
        var app = fixture.Databases.AppConnectionString;
        var id = Guid.CreateVersion7();
        await DbProbe.ExecuteAsync(app, InsertAudit, cancellationToken, id);
        object[] parameters = statement.Contains("$1", StringComparison.Ordinal) ? [id] : [];

        // Act
        var error = await DbProbe.FailsAsync(app, statement, cancellationToken, parameters);

        // Assert
        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, error.SqlState);
        var actor = await DbProbe.ScalarAsync<string>(
            fixture.Databases.OwnerConnectionString, "SELECT actor FROM audit_entries WHERE id = $1", cancellationToken, id);
        Assert.Equal("anonymous", actor);
    }

    [Fact]
    public async Task DeleteOutbox_AsAppUser_Fails42501_AC10()
    {
        // Arrange
        var cancellationToken = TestContext.Current.CancellationToken;
        var app = fixture.Databases.AppConnectionString;

        // Act
        var error = await DbProbe.FailsAsync(app, "DELETE FROM outbox_messages WHERE published_at IS NULL", cancellationToken);

        // Assert
        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, error.SqlState);
    }
}
