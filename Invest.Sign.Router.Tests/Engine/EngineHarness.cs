using Invest.Sign.Router.Core.Abstractions;
using Invest.Sign.Router.Core.Engine;
using Invest.Sign.Router.Core.Model;
using Invest.Sign.Router.Core.Routing;
using Invest.Sign.Router.Tests.Fakes;

namespace Invest.Sign.Router.Tests.Engine;

/// <summary>Связывает движок с фейками в памяти и предоставляет сокращения для управления маршрутом.</summary>
public sealed class EngineHarness
{
    public const string ReplicaA = "replica-a";
    public const string ReplicaB = "replica-b";

    /// <summary>Этап подписания "a" (1 день), затем этап подписания "b" (1 час).</summary>
    public static readonly RouteVersion TwoStageV1 = new(
        1,
        new SigningStage("a", "alice", TimeSpan.FromDays(1)),
        new SigningStage("b", "seal", TimeSpan.FromHours(1)));

    public static readonly SigningRoute TwoStage = new TestRoute("two-stage", TwoStageV1);

    /// <summary>В этап "b" входим, только если атрибут <c>needs-b</c> равен true.</summary>
    public static readonly SigningRoute Conditional = new TestRoute("conditional", new RouteVersion(
        1,
        new SigningStage("a", "alice", TimeSpan.FromDays(1)),
        new SigningStage("b", "bob", TimeSpan.FromDays(1), when: d => d.GetBoolean("needs-b")),
        new SigningStage("c", "seal", TimeSpan.FromHours(1))));

    /// <summary>Этап "x" выполняет <see cref="ScriptedStageHandler"/> (срок 1 час), затем этап подписания "y".</summary>
    public static readonly SigningRoute Custom = new TestRoute("custom", new RouteVersion(
        1,
        new ScriptedStage("x", "x-settings", TimeSpan.FromHours(1)),
        new SigningStage("y", "bob", TimeSpan.FromDays(1))));

    private SigningEngine? _engine;

    public EngineHarness()
    {
        Incoming = new FakeIncomingDocumentSource(Db);
        Handlers = new StageHandlerRegistry(new FakeSigningStageHandler(Db), Scripted);
    }

    public ScriptedStageHandler Scripted { get; } = new();

    /// <summary>Можно дополнять до первого использования <see cref="Engine"/>.</summary>
    public StageHandlerRegistry Handlers { get; }

    public InMemoryDatabase Db { get; } = new();

    public FakeIncomingDocumentSource Incoming { get; }

    public FakeSignedFileSource Files { get; } = new();

    /// <summary>Можно менять до первого использования <see cref="Engine"/>.</summary>
    public EngineOptions Options { get; } = new()
    {
        LeaseDuration = TimeSpan.FromMinutes(5),
        PollInterval = TimeSpan.FromMinutes(1),
        BatchSize = 10,
        DiscoveryBatchSize = 10,
        MaxAttempts = 3,
        RetryBaseDelay = TimeSpan.FromSeconds(10),
        RetryMaxDelay = TimeSpan.FromSeconds(40),
    };

    public RouteRegistry Registry { get; set; } = new([TwoStage, Conditional, Custom]);

    public SigningEngine Engine => _engine ??= CreateEngine(ReplicaA);

    public SigningEngine CreateEngine(
        string replicaId, RouteRegistry? registry = null, IIncomingDocumentSource? incoming = null) =>
        new(new FakeUnitOfWorkFactory(Db), incoming ?? Incoming, Files, Handlers,
            registry ?? Registry, Options, replicaId);

    /// <summary>Добавляет входящий документ и запускает обнаружение. Возвращает идентификатор экземпляра маршрута.</summary>
    public async Task<long> StartDocumentAsync(string docId, string docType, string? attributes = null)
    {
        Incoming.Add(docId, docType, attributes);
        var result = await Engine.DiscoverAsync(CancellationToken.None);
        Assert.Equal(1, result.Created);
        return Db.RouteByDoc(docId).Id;
    }

    public Task<StepResult> StepAsync(long routeId, SigningEngine? engine = null) =>
        (engine ?? Engine).ProcessAsync(routeId, CancellationToken.None);

    public async Task StepAdvancedAsync(long routeId, SigningEngine? engine = null)
    {
        var result = await StepAsync(routeId, engine);
        Assert.True(result.Outcome == StepOutcome.Advanced, $"Expected Advanced, got {result.Outcome}: {result.Error}");
    }

    /// <summary>Входит в следующий этап и создаёт его запрос на подписание. Возвращает этап в состоянии REQUESTED.</summary>
    public async Task<StageInstance> EnterAndRequestNextStageAsync(long routeId)
    {
        await StepAdvancedAsync(routeId);
        Assert.Equal(StageState.Pending, Db.CurrentStage(routeId).State);
        await StepAdvancedAsync(routeId);
        var stage = Db.CurrentStage(routeId);
        Assert.Equal(StageState.Requested, stage.State);
        return stage;
    }
}
