namespace Invest.Sign.Router.Core.Abstractions;

/// <summary>Открывает единицы работы: одно соединение и одна транзакция на единицу.</summary>
public interface IUnitOfWorkFactory
{
    Task<IUnitOfWork> BeginAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Одна транзакция базы данных. Записи движка и собственные записи обработчика этапа
/// (<see cref="IStageHandler.OnTransitionAsync"/>) фиксируются или откатываются вместе.
/// Освобождение без <see cref="CommitAsync"/> выполняет откат.
/// </summary>
public interface IUnitOfWork : IAsyncDisposable
{
    IRouteInstanceStore Store { get; }

    Task CommitAsync(CancellationToken cancellationToken);
}
