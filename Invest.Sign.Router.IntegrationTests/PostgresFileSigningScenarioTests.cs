using System.Security.Cryptography;
using Invest.Sign.Router.Core.Abstractions;
using Invest.Sign.Router.Core.Engine;
using Invest.Sign.Router.Core.Routing;
using Invest.Sign.Router.Infrastructure.Stages;
using Invest.Sign.Router.IntegrationTests.Infrastructure;
using Invest.Sign.Router.PosDb;

namespace Invest.Sign.Router.IntegrationTests;

/// <summary>
/// Реальный путь документа на Postgres: обнаружение, перемещение файла на подписание, ожидание файла и подписи,
/// перемещение в финальный каталог, подпись в <c>sign_signature</c>, история статусов.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class PostgresFileSigningScenarioTests(PostgresFixture db) : IAsyncLifetime
{
    private sealed class DocumentRoute : SigningRoute
    {
        public override string Code => "document";

        public override IReadOnlyList<RouteVersion> Versions { get; } = [new(1, new SignStage())];

        private sealed class SignStage : FileSigningStage
        {
            public override string Key => "sign";

            public override TimeSpan Timeout => TimeSpan.FromDays(1);
        }
    }

    private readonly string _base = Directory.CreateTempSubdirectory("sgn-pg-").FullName;
    private readonly ListIncomingDocumentSource _incoming = new();
    private PostgresUnitOfWorkFactory _factory = null!;
    private FileSigningOptions _dirs = null!;
    private SigningEngine _engine = null!;

    public async Task InitializeAsync()
    {
        if (DockerAvailability.SkipReason is not null)
        {
            return;
        }

        await db.ResetAsync();
        _factory = new PostgresUnitOfWorkFactory(db.Options);
        _dirs = new FileSigningOptions
        {
            SourceDirectory = Path.Combine(_base, "source"),
            SigningDirectory = Path.Combine(_base, "signing"),
            SignedDirectory = Path.Combine(_base, "signed"),
            FinalDirectory = Path.Combine(_base, "final"),
        };
        _engine = new SigningEngine(
            _factory,
            _incoming,
            new NoFiles(),
            new StageHandlerRegistry(new FileSigningStageHandler(_dirs)),
            new RouteRegistry([new DocumentRoute()]),
            new EngineOptions(),
            "replica-a");
    }

    public async Task DisposeAsync()
    {
        if (DockerAvailability.SkipReason is null)
        {
            await _factory.DisposeAsync();
        }

        Directory.Delete(_base, recursive: true);
    }

    [PostgresFact]
    public async Task Document_goes_through_directories_and_its_signature_is_stored()
    {
        Directory.CreateDirectory(_dirs.SourceDirectory);
        await File.WriteAllTextAsync(Path.Combine(_dirs.SourceDirectory, "doc.pdf"), "original");
        _incoming.Documents.Add(new IncomingDocument(
            "doc-1", "document", """{"file_name":"doc.pdf"}""", null, DateTimeOffset.UtcNow));

        var discovery = await _engine.DiscoverAsync(default);
        Assert.Equal(1, discovery.Created);
        var routeId = await db.ScalarAsync<long>("SELECT id FROM sign_document WHERE external_doc_id = 'doc-1'");

        await StepAsync(routeId);   // вход в этап "sign"
        await StepAsync(routeId);   // файл перемещён на подписание, этап ждёт
        Assert.True(File.Exists(Path.Combine(_dirs.SigningDirectory, "doc.pdf")));
        Assert.False(File.Exists(Path.Combine(_dirs.SourceDirectory, "doc.pdf")));

        await StepAsync(routeId);   // подписи ещё нет
        Assert.Equal("REQUESTED", await db.ScalarAsync<string>("SELECT stage_state FROM sign_document"));

        // ПО подписания забирает файл и кладёт подписанный файл и подпись.
        var signature = "detached signature"u8.ToArray();
        Directory.CreateDirectory(_dirs.SignedDirectory);
        File.Move(Path.Combine(_dirs.SigningDirectory, "doc.pdf"), Path.Combine(_dirs.SignedDirectory, "doc.pdf"));
        await File.WriteAllBytesAsync(Path.Combine(_dirs.SignedDirectory, "doc.pdf.sig"), signature);

        await StepAsync(routeId);   // файлы перемещены в финальный каталог, подпись сохранена
        await StepAsync(routeId);   // этапов больше нет: маршрут завершён

        Assert.True(File.Exists(Path.Combine(_dirs.FinalDirectory, "doc.pdf")));
        Assert.True(File.Exists(Path.Combine(_dirs.FinalDirectory, "doc.pdf.sig")));
        Assert.Equal(signature, await db.ScalarAsync<byte[]>("SELECT content FROM sign_signature"));
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(signature)),
            await db.ScalarAsync<string>("SELECT sha256 FROM sign_signature"));

        var history = await new DocumentStatusReader(_factory).GetAsync("doc-1", default);
        Assert.NotNull(history);
        Assert.Equal(("COMPLETED", "sign", "DONE"), (history.Status, history.StageKey, history.StageState));
        Assert.Equal(
            new (string, string, string?)[]
            {
                (AuditEventTypes.RouteCreated, "ACTIVE", null),
                (AuditEventTypes.StageEntered, "ACTIVE", "PENDING"),
                (AuditEventTypes.StageRequested, "ACTIVE", "REQUESTED"),
                (AuditEventTypes.StageDone, "ACTIVE", "DONE"),
                (AuditEventTypes.RouteFinished, "COMPLETED", "DONE"),
            },
            history.History.Select(h => (h.EventType, h.Status, h.StageState)));
    }

    [PostgresFact]
    public async Task Rediscovery_does_not_create_the_document_twice()
    {
        _incoming.Documents.Add(new IncomingDocument("doc-1", "document", null, null, DateTimeOffset.UtcNow));

        Assert.Equal(1, (await _engine.DiscoverAsync(default)).Created);
        var second = await _engine.DiscoverAsync(default);

        Assert.Equal((0, 1), (second.Created, second.AlreadyExisting));
    }

    private async Task StepAsync(long routeId)
    {
        var result = await _engine.ProcessAsync(routeId, default);
        Assert.True(result.Outcome == StepOutcome.Advanced, $"Expected Advanced, got {result.Outcome}: {result.Error}");
    }

    private sealed class ListIncomingDocumentSource : IIncomingDocumentSource
    {
        public List<IncomingDocument> Documents { get; } = [];

        public Task<IReadOnlyList<IncomingDocument>> GetNewAsync(int limit, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<IncomingDocument>>(Documents.Take(limit).ToList());
    }

    private sealed class NoFiles : ISignedFileSource
    {
        public Task<byte[]> ReadAsync(string fileRef, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("The file signing stage passes the signature itself.");
    }
}
