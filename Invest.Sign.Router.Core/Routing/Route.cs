using Invest.Sign.Router.Core.Abstractions;
using Invest.Sign.Router.Core.Model;

namespace Invest.Sign.Router.Core.Routing;

/// <summary>Точка входа fluent API маршрутов.</summary>
/// <example>
/// <code>
/// Route.For("contract", version: 1)
///     .Stage("sign", s => s.Handler&lt;MyStageHandler&gt;(new MySettings("legal")).Timeout(TimeSpan.FromDays(3)))
///     .Stage("director", s => s.Handler&lt;MyStageHandler&gt;(new MySettings("director"))
///         .When(doc => doc.GetDecimal("amount") > 10_000)
///         .Timeout(TimeSpan.FromDays(5)))
///     .Build();
/// </code>
/// </example>
public static class Route
{
    /// <param name="code">Код маршрута, равный типу документа, который обслуживает маршрут.</param>
    /// <param name="version">Версия маршрута, начиная с 1. Новые документы получают наибольшую версию.</param>
    public static RouteBuilder For(string code, int version) => new(code, version);
}

/// <summary>Собирает этапы маршрута. Проверка выполняется в <see cref="Build"/> и сообщает обо всех ошибках сразу.</summary>
public sealed class RouteBuilder
{
    private readonly string _code;
    private readonly int _version;
    private readonly List<StageBuilder> _stages = [];

    internal RouteBuilder(string code, int version)
    {
        _code = code;
        _version = version;
    }

    /// <summary>Добавляет последовательный этап.</summary>
    public RouteBuilder Stage(string key, Action<StageBuilder> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);

        var stage = new StageBuilder(key);
        configure(stage);
        _stages.Add(stage);
        return this;
    }

    /// <summary>Проверяет маршрут и возвращает неизменяемое определение.</summary>
    /// <exception cref="RouteDefinitionException">Маршрут некорректен.</exception>
    public RouteDefinition Build()
    {
        var subject = $"Route '{_code}' v{_version}";
        var errors = new List<string>();

        Identifier.Validate("Route code", _code, RouteLimits.MaxCodeLength, errors);
        if (_version < 1)
        {
            errors.Add($"Version must be 1 or greater, got {_version}.");
        }

        if (_stages.Count == 0)
        {
            errors.Add("Route must have at least one stage.");
        }

        var duplicateKeys = _stages
            .Where(s => s.Key is not null)
            .GroupBy(s => s.Key, StringComparer.Ordinal)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key);
        foreach (var key in duplicateKeys)
        {
            errors.Add($"Stage key '{key}' is used more than once.");
        }

        foreach (var stage in _stages)
        {
            stage.Validate(errors);
        }

        if (errors.Count > 0)
        {
            throw new RouteDefinitionException(subject, errors);
        }

        var stages = _stages.Select(s => s.Build()).ToArray();
        return new RouteDefinition(_code, _version, stages);
    }
}

/// <summary>Настраивает отдельный этап. Обработчик и срок обязательны.</summary>
public sealed class StageBuilder
{
    private readonly List<string> _errors = [];
    private Type? _handlerType;
    private object? _settings;
    private TimeSpan? _timeout;
    private Func<DocumentContext, bool>? _condition;

    internal StageBuilder(string key)
    {
        Key = key;
    }

    internal string Key { get; }

    /// <summary>
    /// Выполняет этап обработчиком <typeparamref name="THandler"/> с настройками <paramref name="settings"/>.
    /// Обработчик должен быть зарегистрирован в <see cref="IStageHandlerResolver"/>; это проверяется при запуске движка.
    /// </summary>
    public StageBuilder Handler<THandler>(object? settings = null)
        where THandler : IStageHandler =>
        SetHandler(typeof(THandler), settings);

    /// <summary>Нетипизированный вариант <see cref="Handler{THandler}"/> для маршрутов-классов.</summary>
    internal StageBuilder Handler(Type handlerType, object? settings)
    {
        if (!typeof(IStageHandler).IsAssignableFrom(handlerType))
        {
            _errors.Add($"Stage '{Key}': handler type {handlerType.Name} does not implement {nameof(IStageHandler)}.");
        }

        return SetHandler(handlerType, settings);
    }

    private StageBuilder SetHandler(Type handlerType, object? settings)
    {
        if (_handlerType is not null)
        {
            _errors.Add($"Stage '{Key}': handler is set more than once.");
        }

        _handlerType = handlerType;
        _settings = settings;
        return this;
    }

    /// <summary>Время, отведённое на запрос на подписание. Обязательно.</summary>
    public StageBuilder Timeout(TimeSpan timeout)
    {
        if (_timeout is not null)
        {
            _errors.Add($"Stage '{Key}': timeout is set more than once.");
        }

        _timeout = timeout;
        return this;
    }

    /// <summary>Условие входа, вычисляемое по атрибутам документа, когда маршрут доходит до этого этапа.</summary>
    public StageBuilder When(Func<DocumentContext, bool> condition)
    {
        ArgumentNullException.ThrowIfNull(condition);

        if (_condition is not null)
        {
            _errors.Add($"Stage '{Key}': condition is set more than once.");
        }

        _condition = condition;
        return this;
    }

    internal void Validate(List<string> errors)
    {
        if (!Identifier.Validate("Stage key", Key, RouteLimits.MaxStageKeyLength, errors))
        {
            return;
        }

        errors.AddRange(_errors);

        if (_handlerType is null)
        {
            errors.Add($"Stage '{Key}': handler is not set, call Handler<T>(...).");
        }

        if (_timeout is null)
        {
            errors.Add($"Stage '{Key}': timeout is not set.");
        }
        else if (_timeout <= TimeSpan.Zero)
        {
            errors.Add($"Stage '{Key}': timeout must be positive, got {_timeout}.");
        }
    }

    internal StageDefinition Build() =>
        new(Key, _handlerType!, _settings, _timeout!.Value, _condition);
}

internal static class Identifier
{
    /// <summary>Проверяет, что идентификатор непустой, не содержит пробелов по краям и помещается в столбец.</summary>
    public static bool Validate(string what, string? value, int maxLength, List<string> errors)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            errors.Add($"{what} must not be empty.");
            return false;
        }

        if (value.Trim().Length != value.Length)
        {
            errors.Add($"{what} '{value}' must not have leading or trailing whitespace.");
            return false;
        }

        if (value.Length > maxLength)
        {
            errors.Add($"{what} '{value}' is longer than {maxLength} characters.");
            return false;
        }

        return true;
    }
}
