namespace Invest.Sign.Router.Core.Abstractions;

/// <summary>Открывает единицы работы. В Oracle: одно соединение и одна транзакция на единицу.</summary>
public interface IUnitOfWorkFactory
{
    Task<IUnitOfWork> BeginAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Одна транзакция базы данных. Хранилище и шлюз подписания используют её совместно, поэтому создание запроса
/// на подписание и перевод этапа в <c>REQUESTED</c> фиксируются или откатываются вместе.
/// Освобождение без <see cref="CommitAsync"/> выполняет откат.
/// </summary>
public interface IUnitOfWork : IAsyncDisposable
{
    IRouteInstanceStore Store { get; }

    ISigningGateway Gateway { get; }

    Task CommitAsync(CancellationToken cancellationToken);
}
