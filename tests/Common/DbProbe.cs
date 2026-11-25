using Npgsql;

namespace SmartParking.Api.Tests.Fixtures;

/// <summary>
/// Plain SQL against the test database, to arrange rows the F2a endpoints cannot create yet and to assert
/// on what was written. Parameters are positional (<c>$1</c>, <c>$2</c>, …).
/// </summary>
internal static class DbProbe
{
    public static async Task<T> ScalarAsync<T>(string connectionString, string sql, CancellationToken cancellationToken, params object[] parameters)
    {
        await using var connection = await OpenAsync(connectionString, cancellationToken);
        await using var command = Command(connection, sql, parameters);
        var value = await command.ExecuteScalarAsync(cancellationToken);
        return (T)value!;
    }

    public static async Task<List<string>> ColumnAsync(string connectionString, string sql, CancellationToken cancellationToken, params object[] parameters)
    {
        await using var connection = await OpenAsync(connectionString, cancellationToken);
        await using var command = Command(connection, sql, parameters);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        List<string> values = [];
        while (await reader.ReadAsync(cancellationToken))
        {
            values.Add(reader.GetString(0));
        }

        return values;
    }

    public static async Task<int> ExecuteAsync(string connectionString, string sql, CancellationToken cancellationToken, params object[] parameters)
    {
        await using var connection = await OpenAsync(connectionString, cancellationToken);
        await using var command = Command(connection, sql, parameters);
        return await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>Runs the statement and returns the PostgreSQL error it must raise.</summary>
    public static Task<PostgresException> FailsAsync(string connectionString, string sql, CancellationToken cancellationToken, params object[] parameters) =>
        Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(connectionString, sql, cancellationToken, parameters));

    private static async Task<NpgsqlConnection> OpenAsync(string connectionString, CancellationToken cancellationToken)
    {
        var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        return connection;
    }

    private static NpgsqlCommand Command(NpgsqlConnection connection, string sql, object[] parameters)
    {
        var command = new NpgsqlCommand(sql, connection);
        foreach (var parameter in parameters)
        {
            command.Parameters.Add(new NpgsqlParameter { Value = parameter });
        }

        return command;
    }
}
