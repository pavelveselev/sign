using Invest.Sign.Router.Core.Abstractions;
using Invest.Sign.Router.Core.Engine;
using Invest.Sign.Router.Core.Routes;
using Invest.Sign.Router.Core.Routing;
using Invest.Sign.Router.Infrastructure.Files;
using Invest.Sign.Router.Infrastructure.Oracle;
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
        services.AddSingleton<IStageHandler, SigningStageHandler>();

        services.AddSingleton(sp => new SigningEngine(
            sp.GetRequiredService<IUnitOfWorkFactory>(),
            sp.GetRequiredService<IIncomingDocumentSource>(),
            sp.GetRequiredService<ISignedFileSource>(),
            sp.GetRequiredService<IStageHandler>(),
            sp.GetRequiredService<RouteRegistry>(),
            sp.GetRequiredService<IOptions<EngineOptions>>().Value,
            sp.GetRequiredService<ReplicaIdentity>().Id));

        services.AddHostedService<Worker>();
        return services;
    }
}
