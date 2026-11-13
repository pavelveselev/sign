namespace Invest.Sign.Router.Core.Abstractions;

/// <summary>Opens units of work. In Oracle: one connection and one transaction per unit.</summary>
public interface IUnitOfWorkFactory
{
    Task<IUnitOfWork> BeginAsync(CancellationToken cancellationToken);
}

/// <summary>
/// One database transaction. The store and the signing gateway share it, so creating a signing
/// request and moving the stage to <c>REQUESTED</c> commit or roll back together.
/// Disposing without <see cref="CommitAsync"/> rolls back.
/// </summary>
public interface IUnitOfWork : IAsyncDisposable
{
    IRouteInstanceStore Store { get; }

    ISigningGateway Gateway { get; }

    Task CommitAsync(CancellationToken cancellationToken);
}
