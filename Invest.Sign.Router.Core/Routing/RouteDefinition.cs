using Invest.Sign.Router.Core.Model;

namespace Invest.Sign.Router.Core.Routing;

/// <summary>An immutable, validated route: an ordered list of sequential stages.</summary>
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

    /// <summary>Route code. Equals the document type the route serves.</summary>
    public string Code { get; }

    public int Version { get; }

    public IReadOnlyList<StageDefinition> Stages { get; }

    public RouteKey Key => new(Code, Version);

    public StageDefinition? FindStage(string key) =>
        _indexByKey.TryGetValue(key, out var index) ? Stages[index] : null;

    /// <summary>
    /// Returns the first stage after <paramref name="currentStageKey"/> (or from the start when null)
    /// whose condition holds for the document. Null means the route is complete.
    /// </summary>
    /// <exception cref="ArgumentException">The current stage key is not part of this route.</exception>
    public StageDefinition? NextStage(string? currentStageKey, DocumentContext document)
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

        for (var i = start; i < Stages.Count; i++)
        {
            if (Stages[i].AppliesTo(document))
            {
                return Stages[i];
            }
        }

        return null;
    }

    public override string ToString() => Key.ToString();
}

/// <summary>Registry key of a route.</summary>
public readonly record struct RouteKey(string Code, int Version)
{
    public override string ToString() => $"{Code}@v{Version}";
}
