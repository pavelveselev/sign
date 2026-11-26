using System.Diagnostics;
using System.Net;
using System.Text.Json;
using Invest.Sign.Router.Core.Routing;
using Invest.Sign.Router.Infrastructure.Stages;
using Invest.Sign.Router.IntegrationTests.Infrastructure;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Invest.Sign.Router.IntegrationTests;

/// <summary>
/// Хост в той же композиции, что и <c>Program</c>: новые документы читаются из вьюхи заглушки Oracle, состояние —
/// в Postgres, документ проходит маршрут по умолчанию <c>document</c> (подписание через каталоги).
/// </summary>
[Collection(OracleCollection.Name)]
public sealed class HostTests(OracleFixture oracle, PostgresFixture pg) : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    private static readonly TimeSpan WaitTimeout = TimeSpan.FromSeconds(60);

    private readonly string _filesRoot = Directory.CreateTempSubdirectory("sgn-host-").FullName;

    private string Dir(string name) => Path.Combine(_filesRoot, name);

    public async Task InitializeAsync()
    {
        if (DockerAvailability.SkipReason is null)
        {
            await oracle.ResetAsync();
            await pg.ResetAsync();
        }
    }

    public Task DisposeAsync()
    {
        Directory.Delete(_filesRoot, recursive: true);
        return Task.CompletedTask;
    }

    [OracleFact]
    public async Task Host_drives_document_through_directories_to_completed()
    {
        using var host = BuildHost();
        await host.StartAsync();
        try
        {
            Directory.CreateDirectory(Dir("source"));
            await File.WriteAllTextAsync(Path.Combine(Dir("source"), "doc-1.pdf"), "original");
            await oracle.AddIncomingDocumentAsync("doc-1", "document", """{"file_name":"doc-1.pdf"}""");

            // Сервис переместил файл на подписание.
            await WaitForAsync(() => Task.FromResult<object?>(
                File.Exists(Path.Combine(Dir("signing"), "doc-1.pdf")) ? true : null));

            // ПО подписания кладёт подписанный файл и подпись.
            Directory.CreateDirectory(Dir("signed"));
            File.Move(Path.Combine(Dir("signing"), "doc-1.pdf"), Path.Combine(Dir("signed"), "doc-1.pdf"));
            await File.WriteAllTextAsync(Path.Combine(Dir("signed"), "doc-1.pdf.sig"), "signature");

            var status = await WaitForAsync(async () =>
            {
                var value = await pg.ScalarAsync<string>("SELECT status FROM sign_document WHERE external_doc_id = 'doc-1'");
                return value == "COMPLETED" ? value : null;
            });
            Assert.Equal("COMPLETED", status);
            Assert.True(File.Exists(Path.Combine(Dir("final"), "doc-1.pdf")));
            Assert.Equal("signature"u8.ToArray(), await pg.ScalarAsync<byte[]>("SELECT content FROM sign_signature"));

            var history = await host.Services.GetRequiredService<DocumentStatusService>().GetStatusHistoryAsync("doc-1");
            Assert.NotNull(history);
            Assert.Equal("COMPLETED", history.Status);
            Assert.Equal("ROUTE_FINISHED", history.History[^1].EventType);
            Assert.All(history.History, h => Assert.StartsWith("test-replica-", h.Actor));

            // То же через HTTP API.
            using var client = Client(host);
            using var response = await client.GetAsync("/api/documents/doc-1/status-history");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var root = json.RootElement;
            Assert.Equal("doc-1", root.GetProperty("docId").GetString());
            Assert.Equal("COMPLETED", root.GetProperty("status").GetString());
            var last = root.GetProperty("history").EnumerateArray().Last();
            Assert.Equal("ROUTE_FINISHED", last.GetProperty("eventType").GetString());
            Assert.Equal("COMPLETED", last.GetProperty("payload").GetProperty("status").GetString());

            using var missing = await client.GetAsync("/api/documents/unknown/status-history");
            Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        }
        finally
        {
            await host.StopAsync();
        }
    }

    [Theory]
    [InlineData("Oracle:ConnectionString", "Oracle: ConnectionString is required.")]
    [InlineData("Postgres:ConnectionString", "Postgres: ConnectionString is required.")]
    [InlineData("FileSigning:FinalDirectory", "FileSigning: FinalDirectory must be an absolute path")]
    public void Host_with_invalid_configuration_fails_to_start(string key, string message)
    {
        using var host = BuildHost(new Dictionary<string, string?> { [key] = "" });

        var error = Assert.ThrowsAny<Exception>(() => host.StartAsync().GetAwaiter().GetResult());
        Assert.Contains(message, error.ToString());
    }

    private static readonly RouteDefinition Subscription = Route.For("subscription", 1)
        .Stage("manual", s => s.CopyFile(targetFolder: "manual").Timeout(TimeSpan.FromDays(1)))
        .Stage("auto", s => s.Http(path: "subscriptions").Timeout(TimeSpan.FromMinutes(10)))
        .Build();

    [Fact]
    public void Host_with_route_using_unregistered_stage_handler_fails_to_start()
    {
        using var host = BuildHost(
            new Dictionary<string, string?> { ["HttpStage:BaseAddress"] = "https://subscriptions.test/api" },
            routes: [Subscription],
            configure: s => s.AddHttpStage());

        var error = Assert.ThrowsAny<Exception>(() => host.StartAsync().GetAwaiter().GetResult());
        Assert.Contains("stage 'manual': handler CopyFileStageHandler is not registered.", error.ToString());
    }

    [Fact]
    public void Host_with_stage_handler_but_without_its_configuration_fails_to_start()
    {
        using var host = BuildHost(routes: [Subscription], configure: s => s.AddCopyFileStage().AddHttpStage());

        var error = Assert.ThrowsAny<Exception>(() => host.StartAsync().GetAwaiter().GetResult());
        Assert.Contains("CopyFile: SourceRoot must be an absolute path", error.ToString());
    }

    [Fact]
    public void Host_with_configured_stage_handlers_builds_the_engine()
    {
        using var host = BuildHost(
            new Dictionary<string, string?>
            {
                ["CopyFile:SourceRoot"] = _filesRoot,
                ["CopyFile:TargetRoot"] = _filesRoot,
                ["HttpStage:BaseAddress"] = "https://subscriptions.test/api",
            },
            routes: [Subscription],
            configure: s => s.AddCopyFileStage().AddHttpStage());

        Assert.NotNull(host.Services.GetRequiredService<Core.Engine.SigningEngine>());
        Assert.NotNull(host.Services.GetRequiredService<DocumentStatusService>());
    }

    /// <summary>Хост как в <c>Program</c>: движок и HTTP API на свободном локальном порту.</summary>
    private WebApplication BuildHost(
        Dictionary<string, string?>? overrides = null,
        IEnumerable<RouteDefinition>? routes = null,
        Action<IServiceCollection>? configure = null)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = Environments.Development,
            ContentRootPath = _filesRoot,
        });
        builder.WebHost.UseUrls("http://127.0.0.1:0");

        // Без Docker строки подключения пустые; для тестов композиции достаточно синтаксически корректных.
        var settings = new Dictionary<string, string?>
        {
            ["Router:TickInterval"] = "00:00:00.200",
            ["Router:ReplicaName"] = "test-replica",
            ["Engine:PollInterval"] = "00:00:01",
            ["Oracle:ConnectionString"] = NonEmpty(oracle.ServiceConnectionString, "User Id=none;Data Source=none"),
            ["Oracle:ExternalSchema"] = OracleFixture.ExternalSchema,
            ["Postgres:ConnectionString"] = NonEmpty(pg.ConnectionString, "Host=none;Database=none"),
            ["FileSigning:SourceDirectory"] = Dir("source"),
            ["FileSigning:SigningDirectory"] = Dir("signing"),
            ["FileSigning:SignedDirectory"] = Dir("signed"),
            ["FileSigning:FinalDirectory"] = Dir("final"),
        };
        foreach (var (key, value) in overrides ?? [])
        {
            settings[key] = value;
        }

        builder.Configuration.AddInMemoryCollection(settings);
        builder.Services.AddSigningRouter(routes);
        configure?.Invoke(builder.Services);
        var app = builder.Build();
        app.MapDocumentStatusEndpoints();
        return app;
    }

    /// <summary>Клиент к HTTP API запущенного хоста.</summary>
    private static HttpClient Client(WebApplication app) =>
        new() { BaseAddress = new Uri(app.Services.GetRequiredService<IServer>().Features
            .GetRequiredFeature<IServerAddressesFeature>().Addresses.First()) };

    private static string NonEmpty(string value, string fallback) => string.IsNullOrEmpty(value) ? fallback : value;

    private static async Task<T> WaitForAsync<T>(Func<Task<T?>> probe)
    {
        var watch = Stopwatch.StartNew();
        while (true)
        {
            if (await probe() is { } value)
            {
                return value;
            }

            if (watch.Elapsed > WaitTimeout)
            {
                throw new TimeoutException($"Condition was not met within {WaitTimeout}.");
            }

            await Task.Delay(200);
        }
    }
}
