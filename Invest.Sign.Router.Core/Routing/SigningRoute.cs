using Invest.Sign.Router.Core.Abstractions;
using Invest.Sign.Router.Core.Model;

namespace Invest.Sign.Router.Core.Routing;

/// <summary>
/// Маршрут типа документа: один класс на тип документа, все версии маршрута — в нём.
/// <see cref="Build"/> проверяет маршрут и возвращает определения всех версий.
/// </summary>
/// <remarks>
/// Экземпляр маршрута запоминает код и версию. Версию, по которой могли быть запущены документы, не редактируют:
/// изменение этапов — новая версия в <see cref="Versions"/>, старая остаётся, пока по ней есть документы в работе.
/// </remarks>
/// <example>
/// <code>
/// public sealed class ContractRoute : SigningRoute
/// {
///     public override string Code => "contract";
///
///     public override IReadOnlyList&lt;RouteVersion&gt; Versions { get; } =
///     [
///         new(1, new LegalStage()),
///         new(2, new LegalStage(), new DirectorStage()),
///     ];
/// }
/// </code>
/// </example>
public abstract class SigningRoute
{
    /// <summary>Код маршрута, равный типу документа (<c>doc_type</c>), который обслуживает маршрут.</summary>
    public abstract string Code { get; }

    /// <summary>Версии маршрута. Новые документы получают наибольшую.</summary>
    public abstract IReadOnlyList<RouteVersion> Versions { get; }

    /// <summary>Проверяет маршрут и возвращает неизменяемые определения всех версий. Сообщает обо всех ошибках сразу.</summary>
    /// <exception cref="RouteDefinitionException">Маршрут некорректен.</exception>
    public IReadOnlyList<RouteDefinition> Build()
    {
        var code = Code;
        var versions = Versions ?? [];
        var errors = new List<string>();

        Identifier.Validate("Route code", code, RouteLimits.MaxCodeLength, errors);

        if (versions.Count == 0)
        {
            errors.Add("Route must have at least one version.");
        }

        foreach (var number in Duplicates(versions.Where(v => v is not null).Select(v => v.Number)))
        {
            errors.Add($"Version {number} is declared more than once.");
        }

        foreach (var version in versions)
        {
            if (version is null)
            {
                errors.Add("Version must not be null.");
                continue;
            }

            ValidateVersion(version, errors);
        }

        if (errors.Count > 0)
        {
            throw new RouteDefinitionException($"Route '{code}' ({GetType().Name})", errors);
        }

        return versions
            .OrderBy(v => v.Number)
            .Select(v => new RouteDefinition(code, v.Number, v.Stages.Select(BuildStage).ToArray()))
            .ToArray();
    }

    private static void ValidateVersion(RouteVersion version, List<string> errors)
    {
        var prefix = $"v{version.Number}";
        if (version.Number < 1)
        {
            errors.Add($"{prefix}: version must be 1 or greater.");
        }

        if (version.Stages.Count == 0)
        {
            errors.Add($"{prefix}: version must have at least one stage.");
        }

        if (version.Stages.Any(s => s is null))
        {
            errors.Add($"{prefix}: stage must not be null.");
            return;
        }

        foreach (var key in Duplicates(version.Stages.Select(s => s.Key).Where(k => k is not null)))
        {
            errors.Add($"{prefix}: stage key '{key}' is used more than once.");
        }

        foreach (var stage in version.Stages)
        {
            var stageErrors = new List<string>();
            if (Identifier.Validate("stage key", stage.Key, RouteLimits.MaxStageKeyLength, stageErrors)
                && stage.Timeout <= TimeSpan.Zero)
            {
                stageErrors.Add($"stage '{stage.Key}': timeout must be positive, got {stage.Timeout}.");
            }

            errors.AddRange(stageErrors.Select(e => $"{prefix}: {e}"));
        }
    }

    private static StageDefinition BuildStage(RouteStage stage) =>
        new(stage.Key, stage.HandlerType, stage.Settings, stage.Timeout, stage.Condition);

    private static IEnumerable<T> Duplicates<T>(IEnumerable<T> items) =>
        items.GroupBy(x => x).Where(g => g.Count() > 1).Select(g => g.Key);

    public override string ToString() => Code;
}

/// <summary>Версия маршрута: номер и этапы в порядке выполнения.</summary>
/// <param name="number">Номер версии, начиная с 1.</param>
/// <param name="stages">Этапы в порядке выполнения; только последовательные.</param>
public sealed class RouteVersion(int number, params IReadOnlyList<RouteStage> stages)
{
    public int Number { get; } = number;

    public IReadOnlyList<RouteStage> Stages { get; } = stages ?? [];

    public override string ToString() => $"v{Number}";
}

/// <summary>Этап маршрута. Наследуйте <see cref="RouteStage{THandler}"/>.</summary>
public abstract class RouteStage
{
    // Наследники — только RouteStage<THandler>: тип обработчика всегда реализует IStageHandler.
    private protected RouteStage()
    {
    }

    /// <summary>Ключ этапа, уникальный в пределах версии маршрута. Хранится в <c>stage_key</c>.</summary>
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
/// <typeparam name="THandler">
/// Обработчик; должен быть зарегистрирован в <see cref="IStageHandlerResolver"/> под своим конкретным типом.
/// Это проверяется при запуске движка.
/// </typeparam>
public abstract class RouteStage<THandler> : RouteStage
    where THandler : IStageHandler
{
    internal sealed override Type HandlerType => typeof(THandler);
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
