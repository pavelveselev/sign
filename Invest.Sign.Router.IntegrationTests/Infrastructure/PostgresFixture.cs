using Invest.Sign.Router.PosDb;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Testcontainers.PostgreSql;

namespace Invest.Sign.Router.IntegrationTests.Infrastructure;

[CollectionDefinition(Name)]
public sealed class PostgresCollection : ICollectionFixture<PostgresFixture>
{
    public const string Name = "postgres";
}

/// <summary>Postgres в контейнере; схема создаётся миграциями EF из <c>Invest.Sign.Router.PosDb</c>.</summary>
public sealed class PostgresFixture : IAsyncLifetime
{
    public const string Image = "postgres:17-alpine";

    private PostgreSqlContainer? _container;

    public string ConnectionString { get; private set; } = "";

    public PostgresOptions Options => new() { ConnectionString = ConnectionString, CommandTimeoutSeconds = 60 };

    public async Task InitializeAsync()
    {
        if (DockerAvailability.SkipReason is not null)
        {
            return;
        }

        _container = new PostgreSqlBuilder(Image).Build();
        await _container.StartAsync();
        ConnectionString = _container.GetConnectionString();

        await using var factory = new PostgresUnitOfWorkFactory(Options);
        await using var db = factory.CreateDbContext();
        await db.Database.MigrateAsync();
    }

    public async Task DisposeAsync()
    {
        NpgsqlConnection.ClearAllPools();
        if (_container is not null)
        {
            await _container.DisposeAsync();
        }
    }

    /// <summary>Удаляет все строки из таблиц сервиса.</summary>
    public Task ResetAsync() =>
        ExecuteAsync("TRUNCATE sign_signature, sign_document_status_history, sign_document RESTART IDENTITY");

    public async Task ExecuteAsync(string sql)
    {
        await using var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }

    public async Task<T?> ScalarAsync<T>(string sql)
    {
        await using var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        var value = await command.ExecuteScalarAsync();
        return value is null or DBNull ? default : (T)value;
    }
}
