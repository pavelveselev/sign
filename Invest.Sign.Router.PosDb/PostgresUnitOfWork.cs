using Invest.Sign.Router.Core.Abstractions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;

namespace Invest.Sign.Router.PosDb;

/// <summary>Открывает единицы работы над базой состояния: один <see cref="SignDbContext"/> и одна транзакция на единицу.</summary>
public sealed class PostgresUnitOfWorkFactory : IUnitOfWorkFactory, IDocumentDbContextFactory, IAsyncDisposable
{
    private readonly NpgsqlDataSource _dataSource;
    private readonly DbContextOptions<SignDbContext> _options;

    public PostgresUnitOfWorkFactory(PostgresOptions options)
    {
        options.EnsureValid();
        _dataSource = NpgsqlDataSource.Create(options.ConnectionString);
        _options = new DbContextOptionsBuilder<SignDbContext>()
            .UseNpgsql(_dataSource, o => o.CommandTimeout(options.CommandTimeoutSeconds))
            .Options;
    }

    public async Task<IUnitOfWork> BeginAsync(CancellationToken cancellationToken)
    {
        var db = CreateDbContext();
        try
        {
            var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
            return new PostgresUnitOfWork(db, transaction);
        }
        catch
        {
            await db.DisposeAsync();
            throw;
        }
    }

    /// <summary>Контекст без транзакции для запросов только на чтение.</summary>
    public SignDbContext CreateDbContext() => new(_options);

    public ValueTask DisposeAsync() => _dataSource.DisposeAsync();
}

/// <summary>Создаёт контексты базы состояния для запросов вне движка.</summary>
public interface IDocumentDbContextFactory
{
    SignDbContext CreateDbContext();
}

internal sealed class PostgresUnitOfWork(SignDbContext db, IDbContextTransaction transaction) : IUnitOfWork
{
    public IRouteInstanceStore Store { get; } = new PostgresRouteInstanceStore(db);

    /// <summary>
    /// Шлюза к ПО подписания в этой базе нет: Oracle используется только для чтения входящих документов,
    /// а этапы, которые вызывают процедуры Oracle в транзакции перехода (<see cref="SigningStageHandler"/>), не поддерживаются.
    /// </summary>
    public ISigningGateway Gateway { get; } = new UnavailableSigningGateway();

    public Task CommitAsync(CancellationToken cancellationToken) => transaction.CommitAsync(cancellationToken);

    public async ValueTask DisposeAsync()
    {
        // Освобождение транзакции без фиксации выполняет откат.
        await transaction.DisposeAsync();
        await db.DisposeAsync();
    }

    private sealed class UnavailableSigningGateway : ISigningGateway
    {
        public Task CreateRequestAsync(SignRequest request, CancellationToken cancellationToken) => throw Unavailable();

        public Task<SignRequestResult?> GetRequestAsync(string requestId, CancellationToken cancellationToken) =>
            throw Unavailable();

        public Task CancelRequestAsync(string requestId, CancellationToken cancellationToken) => throw Unavailable();

        private static NotSupportedException Unavailable() =>
            new("The Postgres state store has no signing gateway: signing requests in the Oracle signing software " +
                "cannot be part of a Postgres transaction. Use a stage handler that does not need the gateway.");
    }
}
