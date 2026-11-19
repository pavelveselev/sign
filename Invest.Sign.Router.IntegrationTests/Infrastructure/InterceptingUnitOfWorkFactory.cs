using Invest.Sign.Router.Core.Abstractions;
using Invest.Sign.Router.Core.Model;

namespace Invest.Sign.Router.IntegrationTests.Infrastructure;

/// <summary>
/// Оборачивает реальную единицу работы Oracle и вызывает <see cref="Before"/> перед выбранными записями в хранилище,
/// чтобы тест мог перехватить аренду или внедрить ошибку посреди реальной транзакции.
/// </summary>
public sealed class InterceptingUnitOfWorkFactory(IUnitOfWorkFactory inner) : IUnitOfWorkFactory
{
    public const string UpdateAndRelease = nameof(UpdateAndRelease);
    public const string UpdateStage = nameof(UpdateStage);

    /// <summary>Вызывается с именем операции; может выбросить исключение. Сбросьте в null внутри, чтобы сработало один раз.</summary>
    public Func<string, Task>? Before { get; set; }

    public async Task<IUnitOfWork> BeginAsync(CancellationToken cancellationToken) =>
        new UnitOfWork(await inner.BeginAsync(cancellationToken), this);

    private Task InvokeAsync(string operation) => Before?.Invoke(operation) ?? Task.CompletedTask;

    private sealed class UnitOfWork(IUnitOfWork inner, InterceptingUnitOfWorkFactory owner) : IUnitOfWork
    {
        public IRouteInstanceStore Store { get; } = new Store(inner.Store, owner);

        public ISigningGateway Gateway => inner.Gateway;

        public Task CommitAsync(CancellationToken cancellationToken) => inner.CommitAsync(cancellationToken);

        public ValueTask DisposeAsync() => inner.DisposeAsync();
    }

    private sealed class Store(IRouteInstanceStore inner, InterceptingUnitOfWorkFactory owner) : IRouteInstanceStore
    {
        public Task<DateTimeOffset> GetNowAsync(CancellationToken ct) => inner.GetNowAsync(ct);

        public Task<long?> TryCreateRouteAsync(NewRouteInstance route, CancellationToken ct) => inner.TryCreateRouteAsync(route, ct);

        public Task<IReadOnlyList<long>> FindCandidatesAsync(int limit, CancellationToken ct) => inner.FindCandidatesAsync(limit, ct);

        public Task<bool> TryAcquireAsync(long routeId, string owner, TimeSpan lease, CancellationToken ct) =>
            inner.TryAcquireAsync(routeId, owner, lease, ct);

        public Task<RouteInstance?> GetRouteAsync(long routeId, CancellationToken ct) => inner.GetRouteAsync(routeId, ct);

        public Task<StageInstance?> GetStageAsync(long stageId, CancellationToken ct) => inner.GetStageAsync(stageId, ct);

        public async Task UpdateAndReleaseAsync(RouteInstance route, string lockOwner, CancellationToken ct)
        {
            await owner.InvokeAsync(UpdateAndRelease);
            await inner.UpdateAndReleaseAsync(route, lockOwner, ct);
        }

        public Task<long> CreateStageAsync(NewStageInstance stage, CancellationToken ct) => inner.CreateStageAsync(stage, ct);

        public async Task UpdateStageAsync(StageInstance stage, CancellationToken ct)
        {
            await owner.InvokeAsync(UpdateStage);
            await inner.UpdateStageAsync(stage, ct);
        }

        public Task SaveArtifactAsync(NewSignedArtifact artifact, CancellationToken ct) => inner.SaveArtifactAsync(artifact, ct);

        public Task AppendAuditAsync(AuditEntry entry, CancellationToken ct) => inner.AppendAuditAsync(entry, ct);
    }
}
