using Invest.Sign.Router.Core.Abstractions;
using Invest.Sign.Router.Core.Engine;
using Invest.Sign.Router.Core.Model;
using Invest.Sign.Router.Core.Routing;
using Invest.Sign.Router.Tests.Fakes;

namespace Invest.Sign.Router.Tests.Engine;

/// <summary>Wires the engine to in-memory fakes and offers shortcuts for driving a route.</summary>
public sealed class EngineHarness
{
    public const string ReplicaA = "replica-a";
    public const string ReplicaB = "replica-b";

    /// <summary>Manual stage "a" (1 day), automatic stage "b" (1 hour).</summary>
    public static readonly RouteDefinition TwoStage = Route.For("two-stage", 1)
        .Stage("a", s => s.Manual("alice").Timeout(TimeSpan.FromDays(1)))
        .Stage("b", s => s.Automatic("seal").Timeout(TimeSpan.FromHours(1)))
        .Build();

    /// <summary>Stage "b" is entered only when attribute <c>needs-b</c> is true.</summary>
    public static readonly RouteDefinition Conditional = Route.For("conditional", 1)
        .Stage("a", s => s.Manual("alice").Timeout(TimeSpan.FromDays(1)))
        .Stage("b", s => s.Manual("bob").When(d => d.GetBoolean("needs-b")).Timeout(TimeSpan.FromDays(1)))
        .Stage("c", s => s.Automatic("seal").Timeout(TimeSpan.FromHours(1)))
        .Build();

    private SigningEngine? _engine;

    public EngineHarness()
    {
        Incoming = new FakeIncomingDocumentSource(Db);
    }

    public InMemoryDatabase Db { get; } = new();

    public FakeIncomingDocumentSource Incoming { get; }

    public FakeSignedFileSource Files { get; } = new();

    /// <summary>Can be changed until <see cref="Engine"/> is first used.</summary>
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

    public RouteRegistry Registry { get; set; } = new([TwoStage, Conditional]);

    public SigningEngine Engine => _engine ??= CreateEngine(ReplicaA);

    public SigningEngine CreateEngine(
        string replicaId, RouteRegistry? registry = null, IIncomingDocumentSource? incoming = null) =>
        new(new FakeUnitOfWorkFactory(Db), incoming ?? Incoming, Files, new SigningStageHandler(),
            registry ?? Registry, Options, replicaId);

    /// <summary>Adds an incoming document and runs discovery. Returns the route instance id.</summary>
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

    /// <summary>Enters the next stage and creates its signing request. Returns the stage in REQUESTED.</summary>
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
