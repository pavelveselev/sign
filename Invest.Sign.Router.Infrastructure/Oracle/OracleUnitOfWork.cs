using System.Data;
using Invest.Sign.Router.Core.Abstractions;
using Oracle.ManagedDataAccess.Client;

namespace Invest.Sign.Router.Infrastructure.Oracle;

/// <summary>Opens a pooled connection and a read-committed transaction per unit of work.</summary>
public sealed class OracleUnitOfWorkFactory : IUnitOfWorkFactory
{
    private readonly OracleOptions _options;

    public OracleUnitOfWorkFactory(OracleOptions options)
    {
        options.EnsureValid();
        _options = options;
    }

    public async Task<IUnitOfWork> BeginAsync(CancellationToken cancellationToken)
    {
        var connection = new OracleConnection(_options.ConnectionString);
        try
        {
            await connection.OpenAsync(cancellationToken);
            var transaction = connection.BeginTransaction(IsolationLevel.ReadCommitted);
            var session = new OracleSession(connection, transaction, _options.CommandTimeoutSeconds);
            return new OracleUnitOfWork(session, _options.ExternalSchema);
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }
}

/// <summary>One Oracle transaction. The store and the gateway share its connection.</summary>
internal sealed class OracleUnitOfWork : IUnitOfWork
{
    private readonly OracleSession _session;
    private bool _committed;

    public OracleUnitOfWork(OracleSession session, string externalSchema)
    {
        _session = session;
        Store = new OracleRouteInstanceStore(session);
        Gateway = new OracleSigningGateway(session, externalSchema);
    }

    public IRouteInstanceStore Store { get; }

    public ISigningGateway Gateway { get; }

    public async Task CommitAsync(CancellationToken cancellationToken)
    {
        await _session.Transaction.CommitAsync(cancellationToken);
        _committed = true;
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (!_committed)
            {
                // Rollback failures (e.g. broken connection) are ignored: the server rolls back on disconnect.
                try
                {
                    await _session.Transaction.RollbackAsync();
                }
                catch (OracleException)
                {
                }
                catch (InvalidOperationException)
                {
                }
            }
        }
        finally
        {
            await _session.Transaction.DisposeAsync();
            await _session.Connection.DisposeAsync();
        }
    }
}
