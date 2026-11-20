using System.Diagnostics;
using System.Security.Cryptography;
using Invest.Sign.Router.IntegrationTests.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Invest.Sign.Router.IntegrationTests;

/// <summary>
/// Этап 6 плана: хост в той же композиции, что и <c>Program</c>, запускается против заглушки и проводит документ
/// по маршруту-примеру <c>invoice</c> (один автоматический этап <c>org-seal</c>).
/// </summary>
[Collection(OracleCollection.Name)]
public sealed class HostTests(OracleFixture db) : IAsyncLifetime
{
    private static readonly TimeSpan WaitTimeout = TimeSpan.FromSeconds(60);

    private readonly string _filesRoot = Directory.CreateTempSubdirectory("sgn-host-").FullName;

    public Task InitializeAsync() => db.ResetAsync();

    public Task DisposeAsync()
    {
        Directory.Delete(_filesRoot, recursive: true);
        return Task.CompletedTask;
    }

    [OracleFact]
    public async Task Host_drives_sample_invoice_to_completed()
    {
        using var host = BuildHost();
        await host.StartAsync();
        try
        {
            await db.AddIncomingDocumentAsync("inv-1", "invoice");

            var requestId = await WaitForAsync(async () =>
                (await db.QueryExternalAsync<string>(
                    "SELECT request_id FROM stub_sign_request WHERE doc_id = 'inv-1' AND kind = 'AUTO' AND signer = 'org-seal'"))
                .SingleOrDefault());

            var content = "signed invoice"u8.ToArray();
            Directory.CreateDirectory(Path.Combine(_filesRoot, "out"));
            await File.WriteAllBytesAsync(Path.Combine(_filesRoot, "out", "inv-1.pdf"), content);
            await db.ExecuteExternalAsync(
                """
                UPDATE stub_sign_request
                   SET status = 'SIGNED', signed_at = SYSTIMESTAMP, file_ref = 'out/inv-1.pdf',
                       file_hash = :fileHash, file_size = :fileSize
                 WHERE request_id = :requestId
                """,
                new { requestId, fileHash = Convert.ToHexStringLower(SHA256.HashData(content)), fileSize = content.Length });

            var status = await WaitForAsync(async () =>
            {
                var value = (await db.QueryServiceAsync<string>(
                    "SELECT status FROM sgn_route_instance WHERE external_doc_id = 'inv-1'")).Single();
                return value == "COMPLETED" ? value : null;
            });
            Assert.Equal("COMPLETED", status);

            var artifacts = await db.QueryServiceAsync<long>("SELECT COUNT(*) FROM sgn_signed_artifact");
            Assert.Equal(1, artifacts.Single());

            var actors = await db.QueryServiceAsync<string>("SELECT DISTINCT actor FROM sgn_audit_event");
            Assert.StartsWith("test-replica-", Assert.Single(actors));
        }
        finally
        {
            await host.StopAsync();
        }
    }

    [Fact]
    public void Host_with_invalid_configuration_fails_to_start()
    {
        using var host = BuildHost(new Dictionary<string, string?> { ["Oracle:ConnectionString"] = "" });

        var error = Assert.ThrowsAny<Exception>(() => host.StartAsync().GetAwaiter().GetResult());
        Assert.Contains("Oracle: ConnectionString is required.", error.ToString());
    }

    private IHost BuildHost(Dictionary<string, string?>? overrides = null)
    {
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
        {
            DisableDefaults = true,
            EnvironmentName = Environments.Development,
        });

        var settings = new Dictionary<string, string?>
        {
            ["Router:TickInterval"] = "00:00:00.200",
            ["Router:ReplicaName"] = "test-replica",
            ["Engine:PollInterval"] = "00:00:01",
            ["Oracle:ConnectionString"] = db.ServiceConnectionString,
            ["Oracle:ExternalSchema"] = OracleFixture.ExternalSchema,
            ["SignedFiles:Root"] = _filesRoot,
        };
        foreach (var (key, value) in overrides ?? [])
        {
            settings[key] = value;
        }

        builder.Configuration.AddInMemoryCollection(settings);
        builder.Services.AddSigningRouter();
        return builder.Build();
    }

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
