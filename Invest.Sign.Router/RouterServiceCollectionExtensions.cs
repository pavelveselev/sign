using Invest.Sign.Router.Core.Abstractions;
using Invest.Sign.Router.Core.Discovery;
using Invest.Sign.Router.Core.Engine;
using Invest.Sign.Router.Core.Routing;
using Invest.Sign.Router.Infrastructure.Files;
using Invest.Sign.Router.Infrastructure.Stages;
using Invest.Sign.Router.OracleDb;
using Invest.Sign.Router.PosDb;
using Microsoft.Extensions.Options;

namespace Invest.Sign.Router;

public static class RouterServiceCollectionExtensions
{
    /// <summary>
    /// Регистрирует движок маршрутов и цикл тактов. Состояние документов, история статусов и подписи — в Postgres;
    /// Oracle — только источник новых документов (вьюха входящих). По умолчанию подключён этап подписания через каталоги.
    /// </summary>
    /// <param name="routes">Классы маршрутов, по одному на тип документа; по умолчанию — <see cref="DefaultRoutes.All"/>.</param>
    public static IServiceCollection AddSigningRouter(
        this IServiceCollection services, IEnumerable<SigningRoute>? routes = null)
    {
        // Настройки: привязка из конфигурации и проверка при старте, чтобы ошибка конфигурации останавливала запуск,
        // а не всплывала на первом такте.
        services.AddValidatedOptions<RouterOptions>(RouterOptions.SectionName, o => o.Validate());
        services.AddValidatedOptions<EngineOptions>(RouterOptions.EngineSectionName, o => o.Validate());
        services.AddValidatedOptions<OracleOptions>(OracleOptions.SectionName, o => o.Validate());
        services.AddValidatedOptions<PostgresOptions>(PostgresOptions.SectionName, o => o.Validate());

        // Хост ждёт завершения текущего шага не дольше этого времени, затем шаг прерывается.
        services.AddOptions<HostOptions>()
            .Configure<IOptions<RouterOptions>>((host, router) => host.ShutdownTimeout = router.Value.ShutdownTimeout);

        // Реестр строится сразу: некорректный маршрут или два класса на один тип документа останавливают запуск.
        services.AddSingleton(new RouteRegistry(routes ?? DefaultRoutes.All));

        services.AddSingleton(sp => ReplicaIdentity.Create(sp.GetRequiredService<IOptions<RouterOptions>>().Value));

        // База состояния: одна фабрика (и один пул соединений) на процесс.
        services.AddSingleton(sp => new PostgresUnitOfWorkFactory(sp.GetRequiredService<IOptions<PostgresOptions>>().Value));
        services.AddSingleton<IUnitOfWorkFactory>(sp => sp.GetRequiredService<PostgresUnitOfWorkFactory>());
        services.AddSingleton<IDocumentDbContextFactory>(sp => sp.GetRequiredService<PostgresUnitOfWorkFactory>());
        services.AddSingleton<DocumentStatusReader>();
        services.AddSingleton<DocumentStatusService>();

        // Новые документы: лента вьюхи Oracle минус документы, уже известные базе состояния.
        services.AddSingleton<IIncomingDocumentSource>(sp => new WatermarkIncomingDocumentSource(
            new OracleIncomingDocumentFeed(sp.GetRequiredService<IOptions<OracleOptions>>().Value),
            new PostgresKnownDocuments(sp.GetRequiredService<IDocumentDbContextFactory>()),
            sp.GetRequiredService<IOptions<RouterOptions>>().Value.DiscoveryLookback));

        // Этапы по умолчанию передают файл результата сами; чтение с общего ресурса подключается AddSignedFiles().
        services.AddSingleton<ISignedFileSource, UnavailableSignedFileSource>();

        // Обработчики этапов регистрируются под своим конкретным типом: этап ссылается на него через RouteStage<T>.
        // Если маршрут ссылается на незарегистрированный обработчик, движок не создастся и хост не запустится.
        services.AddFileSigningStage();
        services.AddSingleton<IStageHandlerResolver>(sp => new ServiceProviderStageHandlerResolver(sp));

        services.AddSingleton(sp => new SigningEngine(
            sp.GetRequiredService<IUnitOfWorkFactory>(),
            sp.GetRequiredService<IIncomingDocumentSource>(),
            sp.GetRequiredService<ISignedFileSource>(),
            sp.GetRequiredService<IStageHandlerResolver>(),
            sp.GetRequiredService<RouteRegistry>(),
            sp.GetRequiredService<IOptions<EngineOptions>>().Value,
            sp.GetRequiredService<ReplicaIdentity>().Id));

        services.AddHostedService<Worker>();
        return services;
    }

    /// <summary>
    /// Регистрирует <see cref="FileSigningStageHandler"/> для этапов <see cref="FileSigningStage"/>. Настройки — секция
    /// <c>FileSigning</c>. Вызывается из <see cref="AddSigningRouter"/>.
    /// </summary>
    private static IServiceCollection AddFileSigningStage(this IServiceCollection services)
    {
        services.AddValidatedOptions<FileSigningOptions>(FileSigningOptions.SectionName, o => o.Validate());
        services.AddSingleton(sp =>
            new FileSigningStageHandler(sp.GetRequiredService<IOptions<FileSigningOptions>>().Value));
        return services;
    }

    /// <summary>
    /// Подключает чтение файлов результата с общего ресурса (<see cref="FileSystemSignedFileSource"/>) для обработчиков,
    /// которые возвращают только ссылку на файл. Настройки — секция <c>SignedFiles</c>.
    /// </summary>
    public static IServiceCollection AddSignedFiles(this IServiceCollection services)
    {
        services.AddValidatedOptions<SignedFilesOptions>(SignedFilesOptions.SectionName, o => o.Validate());
        services.AddSingleton<ISignedFileSource>(sp =>
            new FileSystemSignedFileSource(sp.GetRequiredService<IOptions<SignedFilesOptions>>().Value));
        return services;
    }

    /// <summary>
    /// Регистрирует <see cref="CopyFileStageHandler"/> для этапов <see cref="CopyFileStage"/>. Настройки — секция <c>CopyFile</c>.
    /// </summary>
    public static IServiceCollection AddCopyFileStage(this IServiceCollection services)
    {
        services.AddValidatedOptions<CopyFileOptions>(CopyFileOptions.SectionName, o => o.Validate());
        services.AddSingleton(sp => new CopyFileStageHandler(sp.GetRequiredService<IOptions<CopyFileOptions>>().Value));
        return services;
    }

    /// <summary>
    /// Регистрирует <see cref="HttpStageHandler"/> для этапов <see cref="HttpStage"/>. Настройки — секция <c>HttpStage</c>.
    /// </summary>
    public static IServiceCollection AddHttpStage(this IServiceCollection services)
    {
        services.AddValidatedOptions<HttpStageOptions>(HttpStageOptions.SectionName, o => o.Validate());
        services.AddSingleton(sp =>
        {
            var options = sp.GetRequiredService<IOptions<HttpStageOptions>>().Value;

            // Один клиент на всё время жизни процесса; PooledConnectionLifetime периодически пересоздаёт соединения,
            // чтобы изменения DNS подхватывались без IHttpClientFactory.
            var client = new HttpClient(new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(2) })
            {
                BaseAddress = options.GetBaseUri(),
                Timeout = options.Timeout,
            };
            return new HttpStageHandler(client);
        });
        return services;
    }

    /// <summary>Источник по умолчанию: обработчик, вернувший только ссылку на файл, получает понятную ошибку.</summary>
    private sealed class UnavailableSignedFileSource : ISignedFileSource
    {
        public Task<byte[]> ReadAsync(string fileRef, CancellationToken cancellationToken) =>
            throw new InvalidOperationException(
                $"Stage result file '{fileRef}' cannot be read: no signed file source is configured (call AddSignedFiles()).");
    }
}
