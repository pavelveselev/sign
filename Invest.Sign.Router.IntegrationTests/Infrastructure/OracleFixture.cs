using Dapper;
using Invest.Sign.Router.OracleDb;
using Oracle.ManagedDataAccess.Client;
using Testcontainers.Oracle;

namespace Invest.Sign.Router.IntegrationTests.Infrastructure;

[CollectionDefinition(Name)]
public sealed class OracleCollection : ICollectionFixture<OracleFixture>
{
    public const string Name = "oracle";
}

/// <summary>
/// Oracle в контейнере с двумя схемами: <c>SGN_EXT</c> (заглушка внешней стороны, db/stub) и
/// <c>SGN_SERVICE</c> — учётная запись сервиса, которая только читает вьюху входящих документов.
/// </summary>
public sealed class OracleFixture : IAsyncLifetime
{
    public const string Image = "gvenzl/oracle-xe:21.3.0-slim-faststart";
    public const string ExternalSchema = "SGN_EXT";
    public const string ServiceSchema = "SGN_SERVICE";

    private OracleContainer? _container;

    public string ServiceConnectionString { get; private set; } = "";

    public string ExternalConnectionString { get; private set; } = "";

    public OracleOptions Options => new()
    {
        ConnectionString = ServiceConnectionString,
        ExternalSchema = ExternalSchema,
        CommandTimeoutSeconds = 60,
    };

    public async Task InitializeAsync()
    {
        if (DockerAvailability.SkipReason is not null)
        {
            return;
        }

        _container = new OracleBuilder(Image).Build();
        await _container.StartAsync();

        var admin = new OracleConnectionStringBuilder(_container.GetConnectionString()) { UserID = "system" };
        var servicePassword = NewPassword();
        var externalPassword = NewPassword();

        await using (var connection = new OracleConnection(admin.ConnectionString))
        {
            await connection.OpenAsync();
            foreach (var sql in new[]
            {
                $"CREATE USER {ExternalSchema} IDENTIFIED BY \"{externalPassword}\" QUOTA UNLIMITED ON users",
                $"CREATE USER {ServiceSchema} IDENTIFIED BY \"{servicePassword}\" QUOTA UNLIMITED ON users",
                $"GRANT CREATE SESSION, CREATE TABLE, CREATE VIEW TO {ExternalSchema}",
                $"GRANT CREATE SESSION TO {ServiceSchema}",
            })
            {
                await connection.ExecuteAsync(sql);
            }
        }

        ExternalConnectionString = new OracleConnectionStringBuilder(admin.ConnectionString)
        {
            UserID = ExternalSchema,
            Password = externalPassword,
        }.ConnectionString;
        ServiceConnectionString = new OracleConnectionStringBuilder(admin.ConnectionString)
        {
            UserID = ServiceSchema,
            Password = servicePassword,
        }.ConnectionString;

        var db = FindDbFolder();
        await SqlScriptRunner.RunFolderAsync(
            ExternalConnectionString,
            Path.Combine(db, "stub"),
            new Dictionary<string, string> { ["service_schema"] = ServiceSchema });
    }

    public async Task DisposeAsync()
    {
        OracleConnection.ClearAllPools();
        if (_container is not null)
        {
            await _container.DisposeAsync();
        }
    }

    /// <summary>Удаляет все строки из таблицы заглушки.</summary>
    public async Task ResetAsync()
    {
        await using var external = new OracleConnection(ExternalConnectionString);
        await external.ExecuteAsync("DELETE FROM stub_incoming_document");
    }

    /// <summary>Выполняет оператор от имени внешней стороны и фиксирует транзакцию.</summary>
    public async Task ExecuteExternalAsync(string sql, object? parameters = null)
    {
        await using var connection = new OracleConnection(ExternalConnectionString);
        await connection.ExecuteAsync(sql, parameters);
    }

    public Task AddIncomingDocumentAsync(string docId, string docType, string? attributes = null) =>
        ExecuteExternalAsync(
            "INSERT INTO stub_incoming_document (doc_id, doc_type, attributes) VALUES (:docId, :docType, :attributes)",
            new { docId, docType, attributes });

    private static string NewPassword() => "P" + Guid.NewGuid().ToString("N")[..20];

    private static string FindDbFolder()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "db");
            if (Directory.Exists(Path.Combine(candidate, "stub")))
            {
                return candidate;
            }
        }

        throw new DirectoryNotFoundException("Repository folder 'db' is not found above the test output directory.");
    }
}
