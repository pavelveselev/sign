using Invest.Sign.Router.Core.Model;

namespace Invest.Sign.Router.Core.Routing;

/// <summary>Неизменяемый проверенный маршрут: упорядоченный список последовательных этапов.</summary>
public sealed class RouteDefinition
{
    private readonly Dictionary<string, int> _indexByKey;

    internal RouteDefinition(string code, int version, IReadOnlyList<StageDefinition> stages)
    {
        Code = code;
        Version = version;
        Stages = stages;
        _indexByKey = stages
            .Select((stage, index) => (stage.Key, index))
            .ToDictionary(x => x.Key, x => x.index, StringComparer.Ordinal);
    }

    /// <summary>Код маршрута. Равен типу документа, который обслуживает маршрут.</summary>
    public string Code { get; }

    public int Version { get; }

    public IReadOnlyList<StageDefinition> Stages { get; }

    public RouteKey Key => new(Code, Version);

    public StageDefinition? FindStage(string key) =>
        _indexByKey.TryGetValue(key, out var index) ? Stages[index] : null;

    /// <summary>
    /// Возвращает первый этап после <paramref name="currentStageKey"/> (или с начала, если null),
    /// условие которого выполняется для документа. Null означает, что маршрут завершён.
    /// </summary>
    /// <exception cref="ArgumentException">Ключ текущего этапа не входит в этот маршрут.</exception>
    /// <exception cref="StageConditionException">Условие этапа выбросило исключение.</exception>
    public StageDefinition? NextStage(string? currentStageKey, DocumentContext document) =>
        SelectNextStage(currentStageKey, document).Next;

    /// <summary>
    /// Как <see cref="NextStage"/>, но также возвращает этапы, пропущенные из-за невыполненного условия.
    /// </summary>
    /// <exception cref="ArgumentException">Ключ текущего этапа не входит в этот маршрут.</exception>
    /// <exception cref="StageConditionException">Условие этапа выбросило исключение.</exception>
    public StageSelection SelectNextStage(string? currentStageKey, DocumentContext document)
    {
        ArgumentNullException.ThrowIfNull(document);

        var start = 0;
        if (currentStageKey is not null)
        {
            if (!_indexByKey.TryGetValue(currentStageKey, out var currentIndex))
            {
                throw new ArgumentException(
                    $"Stage '{currentStageKey}' is not defined in route {Key}.", nameof(currentStageKey));
            }

            start = currentIndex + 1;
        }

        var skipped = new List<StageDefinition>();
        for (var i = start; i < Stages.Count; i++)
        {
            if (Stages[i].AppliesTo(document))
            {
                return new StageSelection(Stages[i], skipped);
            }

            skipped.Add(Stages[i]);
        }

        return new StageSelection(null, skipped);
    }

    public override string ToString() => Key.ToString();
}

/// <summary>Результат выбора следующего этапа: этап для входа (null = маршрут завершён) и пропущенные этапы.</summary>
public sealed record StageSelection(StageDefinition? Next, IReadOnlyList<StageDefinition> Skipped);

/// <summary>Ключ маршрута в реестре.</summary>
public readonly record struct RouteKey(string Code, int Version)
{
    public override string ToString() => $"{Code}@v{Version}";
}
