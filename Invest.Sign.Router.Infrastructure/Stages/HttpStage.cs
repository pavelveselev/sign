using Invest.Sign.Router.Core.Routing;

namespace Invest.Sign.Router.Infrastructure.Stages;

/// <summary>
/// Этап, выполняемый HTTP-сервисом (<see cref="HttpStageHandler"/>). Адрес сервиса — секция <c>HttpStage</c>,
/// обработчик подключается <c>AddHttpStage()</c>.
/// </summary>
public abstract class HttpStage : RouteStage<HttpStageHandler>
{
    /// <summary>Путь ресурса относительно <c>HttpStage:BaseAddress</c>, например <c>subscriptions</c>.</summary>
    protected abstract string Path { get; }

    public sealed override object? Settings => new HttpStageSettings(Path);
}
