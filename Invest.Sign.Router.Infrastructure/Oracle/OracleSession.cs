using System.Data;
using Dapper;
using Oracle.ManagedDataAccess.Client;

namespace Invest.Sign.Router.Infrastructure.Oracle;

/// <summary>
/// A connection with an open transaction, shared by the store and the gateway of one unit of work.
/// Reads go through Dapper; writes use explicit typed parameters.
/// </summary>
/// <remarks>
/// Timestamps cross the boundary in UTC: columns are read as <c>SYS_EXTRACT_UTC(col)</c> and written as
/// <c>FROM_TZ(:p, '+00:00')</c> with a <c>TIMESTAMP</c> parameter, so the replica time zone never matters.
/// </remarks>
internal sealed class OracleSession(OracleConnection connection, OracleTransaction transaction, int commandTimeoutSeconds)
{
    public OracleConnection Connection => connection;

    public OracleTransaction Transaction => transaction;

    public Task<IEnumerable<T>> QueryAsync<T>(string sql, object? parameters, CancellationToken ct) =>
        connection.QueryAsync<T>(new CommandDefinition(sql, parameters, transaction, commandTimeoutSeconds, cancellationToken: ct));

    public Task<T?> QuerySingleOrDefaultAsync<T>(string sql, object? parameters, CancellationToken ct) =>
        connection.QuerySingleOrDefaultAsync<T>(new CommandDefinition(sql, parameters, transaction, commandTimeoutSeconds, cancellationToken: ct));

    public async Task<int> ExecuteAsync(string sql, CancellationToken ct, params OracleParameter[] parameters)
    {
        await using var command = CreateCommand(sql, parameters);
        return await command.ExecuteNonQueryAsync(ct);
    }

    public OracleCommand CreateCommand(string sql, params OracleParameter[] parameters)
    {
        var command = connection.CreateCommand();
        command.CommandText = sql;
        command.BindByName = true;
        command.Transaction = transaction;
        command.CommandTimeout = commandTimeoutSeconds;
        command.Parameters.AddRange(parameters);
        return command;
    }
}

internal static class P
{
    public static OracleParameter Str(string name, string? value) =>
        new(name, OracleDbType.Varchar2) { Value = (object?)value ?? DBNull.Value };

    public static OracleParameter Long(string name, long? value) =>
        new(name, OracleDbType.Int64) { Value = (object?)value ?? DBNull.Value };

    public static OracleParameter Int(string name, int? value) =>
        new(name, OracleDbType.Int32) { Value = (object?)value ?? DBNull.Value };

    /// <summary>UTC timestamp; use as <c>FROM_TZ(:name, '+00:00')</c> in SQL.</summary>
    public static OracleParameter Utc(string name, DateTimeOffset? value) =>
        new(name, OracleDbType.TimeStamp) { Value = value is { } v ? v.UtcDateTime : DBNull.Value };

    public static OracleParameter Clob(string name, string? value) =>
        new(name, OracleDbType.Clob) { Value = (object?)value ?? DBNull.Value };

    public static OracleParameter Blob(string name, byte[] value) =>
        new(name, OracleDbType.Blob) { Value = value };

    public static OracleParameter OutLong(string name) =>
        new(name, OracleDbType.Int64) { Direction = ParameterDirection.Output };
}

internal static class OracleTime
{
    /// <summary>Converts a value read via <c>SYS_EXTRACT_UTC</c> to <see cref="DateTimeOffset"/>.</summary>
    public static DateTimeOffset FromUtc(DateTime value) =>
        new(DateTime.SpecifyKind(value, DateTimeKind.Utc));

    public static DateTimeOffset? FromUtc(DateTime? value) =>
        value is { } v ? FromUtc(v) : null;
}
