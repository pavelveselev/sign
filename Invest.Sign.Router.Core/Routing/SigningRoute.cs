using Invest.Sign.Router.Core.Abstractions;
using Invest.Sign.Router.Core.Model;

namespace Invest.Sign.Router.Core.Routing;

/// <summary>
/// Маршрут, записанный классом: код, версия и этапы задаются переопределением свойств. Альтернатива fluent API
/// <see cref="Route.For"/>; <see cref="Build"/> выполняет те же проверки и возвращает тот же <see cref="RouteDefinition"/>.
/// </summary>
/// <example>
/// <code>
/// public sealed class ContractRouteV1 : SigningRoute
/// {
///     public override string Code => "contract";
///     public override int Version => 1;
///     public override IReadOnlyList&lt;RouteStage&gt; Stages { get; } = [new LegalStage(), new DirectorStage()];
/// }
/// </code>
/// </example>
public abstract class SigningRoute
{
    /// <summary>Код маршрута, равный типу документа (<c>doc_type</c>), который обслуживает маршрут.</summary>
    public abstract string Code { get; }

    /// <summary>Версия маршрута, начиная с 1. Новые документы получают наибольшую версию.</summary>
    public abstract int Version { get; }

    /// <summary>Этапы в порядке выполнения.</summary>
    public abstract IReadOnlyList<RouteStage> Stages { get; }

    /// <summary>Проверяет маршрут и возвращает неизменяемое определение.</summary>
    /// <exception cref="RouteDefinitionException">Маршрут некорректен.</exception>
    public RouteDefinition Build()
    {
        var builder = Route.For(Code, Version);
        foreach (var stage in Stages)
        {
            builder.Stage(stage.Key, s =>
            {
                s.Handler(stage.HandlerType, stage.Settings).Timeout(stage.Timeout);
                if (stage.Condition is { } condition)
                {
                    s.When(condition);
                }
            });
        }

        return builder.Build();
    }
}

/// <summary>Этап маршрута-класса. Наследуйте <see cref="RouteStage{THandler}"/>.</summary>
public abstract class RouteStage
{
    // Наследники — только RouteStage<THandler>: тип обработчика всегда реализует IStageHandler.
    private protected RouteStage()
    {
    }

    /// <summary>Ключ этапа, уникальный в пределах маршрута. Хранится в <c>stage_key</c>.</summary>
    public abstract string Key { get; }

    /// <summary>Сколько этап может ждать результата (<c>REQUESTED</c>), отсчитывая с момента запуска.</summary>
    public abstract TimeSpan Timeout { get; }

    /// <summary>Настройки, передаваемые обработчику в <see cref="StageContext.Settings"/>.</summary>
    public virtual object? Settings => null;

    /// <summary>Условие входа по атрибутам документа; null — этап выполняется всегда.</summary>
    public virtual Func<DocumentContext, bool>? Condition => null;

    internal abstract Type HandlerType { get; }

    public override string ToString() => Key;
}

/// <summary>Этап, выполняемый обработчиком <typeparamref name="THandler"/>.</summary>
/// <typeparam name="THandler">Обработчик; должен быть зарегистрирован в DI под своим конкретным типом.</typeparam>
public abstract class RouteStage<THandler> : RouteStage
    where THandler : IStageHandler
{
    internal sealed override Type HandlerType => typeof(THandler);
}
