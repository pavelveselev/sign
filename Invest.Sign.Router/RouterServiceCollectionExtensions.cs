using Invest.Sign.Router.Core.Abstractions;
using Invest.Sign.Router.Core.Engine;
using Invest.Sign.Router.Core.Routes;
using Invest.Sign.Router.Core.Routing;
using Invest.Sign.Router.Infrastructure.Files;
using Invest.Sign.Router.Infrastructure.Stages;
using Invest.Sign.Router.OracleDb;
using Microsoft.Extensions.Options;

namespace Invest.Sign.Router;

public static class RouterServiceCollectionExtensions
{
    /// <summary>
    /// Регистрирует движок маршрутов, его зависимости на Oracle и файловой системе и цикл тактов.
    /// </summary>
    /// <param name="routes">Определения маршрутов; по умолчанию — маршруты-примеры <see cref="SampleRoutes.All"/>.</param>
    public static IServiceCollection AddSigningRouter(
        this IServiceCollection services, IEnumerable<RouteDefinition>? routes = null)
    {
        // Настройки: привязка из конфигурации и проверка при старте, чтобы ошибка конфигурации останавливала запуск,
        // а не всплывала на первом такте.
        services.AddValidatedOptions<RouterOptions>(RouterOptions.SectionName, o => o.Validate());
        services.AddValidatedOptions<EngineOptions>(RouterOptions.EngineSectionName, o => o.Validate());
        services.AddValidatedOptions<OracleOptions>(OracleOptions.SectionName, o => o.Validate());
        services.AddValidatedOptions<SignedFilesOptions>(SignedFilesOptions.SectionName, o => o.Validate());

        // Хост ждёт завершения текущего шага не дольше этого времени, затем шаг прерывается.
        services.AddOptions<HostOptions>()
            .Configure<IOptions<RouterOptions>>((host, router) => host.ShutdownTimeout = router.Value.ShutdownTimeout);

        // Пока реальные типы документов неизвестны (открытый вопрос 9), по умолчанию работают маршруты-примеры.
        // Реестр строится сразу: дубликат (код, версия) должен остановить запуск.
        services.AddSingleton(new RouteRegistry(routes ?? SampleRoutes.All));

        services.AddSingleton(sp => ReplicaIdentity.Create(sp.GetRequiredService<IOptions<RouterOptions>>().Value));

        services.AddSingleton<IUnitOfWorkFactory>(sp =>
            new OracleUnitOfWorkFactory(sp.GetRequiredService<IOptions<OracleOptions>>().Value));
        services.AddSingleton<IIncomingDocumentSource>(sp =>
            new OracleIncomingDocumentSource(sp.GetRequiredService<IOptions<OracleOptions>>().Value));
        services.AddSingleton<ISignedFileSource>(sp =>
            new FileSystemSignedFileSource(sp.GetRequiredService<IOptions<SignedFilesOptions>>().Value));
        // Обработчики этапов регистрируются под своим конкретным типом: этап ссылается на него в Handler<T>().
        // Если маршрут ссылается на незарегистрированный обработчик, движок не создастся и хост не запустится.
        services.AddSingleton<SigningStageHandler>();
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
    /// Регистрирует <see cref="CopyFileStageHandler"/> для этапов <c>CopyFile(...)</c>. Настройки — секция <c>CopyFile</c>.
    /// </summary>
    public static IServiceCollection AddCopyFileStage(this IServiceCollection services)
    {
        services.AddValidatedOptions<CopyFileOptions>(CopyFileOptions.SectionName, o => o.Validate());
        services.AddSingleton(sp => new CopyFileStageHandler(sp.GetRequiredService<IOptions<CopyFileOptions>>().Value));
        return services;
    }

    /// <summary>
    /// Регистрирует <see cref="HttpStageHandler"/> для этапов <c>Http(...)</c>. Настройки — секция <c>HttpStage</c>.
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
}
