using Invest.Sign.Router.Core.Model;

namespace Invest.Sign.Router.Core.Routing;

/// <summary>Entry point of the fluent route API.</summary>
/// <example>
/// <code>
/// Route.For("contract", version: 1)
///     .Stage("legal-review", s => s.Manual(signer: "legal").Timeout(TimeSpan.FromDays(3)))
///     .Stage("director", s => s.Manual(signer: "director")
///         .When(doc => doc.GetDecimal("amount") > 10_000)
///         .Timeout(TimeSpan.FromDays(5)))
///     .Build();
/// </code>
/// </example>
public static class Route
{
    /// <param name="code">Route code, equal to the document type the route serves.</param>
    /// <param name="version">Route version, starting from 1. New documents get the highest version.</param>
    public static RouteBuilder For(string code, int version) => new(code, version);
}

/// <summary>Collects stages of a route. Validation happens in <see cref="Build"/> and reports all errors at once.</summary>
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

    /// <summary>Appends a sequential stage.</summary>
    public RouteBuilder Stage(string key, Action<StageBuilder> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);

        var stage = new StageBuilder(key);
        configure(stage);
        _stages.Add(stage);
        return this;
    }

    /// <summary>Validates the route and returns an immutable definition.</summary>
    /// <exception cref="RouteDefinitionException">The route is invalid.</exception>
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

/// <summary>Configures a single stage. Signing kind and timeout are mandatory.</summary>
public sealed class StageBuilder
{
    private readonly List<string> _errors = [];
    private SigningKind? _kind;
    private string? _signer;
    private TimeSpan? _timeout;
    private Func<DocumentContext, bool>? _condition;

    internal StageBuilder(string key)
    {
        Key = key;
    }

    internal string Key { get; }

    /// <summary>Signing by an employee; the signing software notifies the signer.</summary>
    public StageBuilder Manual(string signer) => SetKind(SigningKind.Manual, signer);

    /// <summary>Automatic signing by the signing software.</summary>
    public StageBuilder Automatic(string signer) => SetKind(SigningKind.Auto, signer);

    /// <summary>Time allowed for the signing request. Mandatory.</summary>
    public StageBuilder Timeout(TimeSpan timeout)
    {
        if (_timeout is not null)
        {
            _errors.Add($"Stage '{Key}': timeout is set more than once.");
        }

        _timeout = timeout;
        return this;
    }

    /// <summary>Entry condition, evaluated on document attributes when the route reaches this stage.</summary>
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

    private StageBuilder SetKind(SigningKind kind, string signer)
    {
        if (_kind is not null)
        {
            _errors.Add($"Stage '{Key}': signing kind is set more than once.");
        }

        _kind = kind;
        _signer = signer;
        return this;
    }

    internal void Validate(List<string> errors)
    {
        if (!Identifier.Validate("Stage key", Key, RouteLimits.MaxStageKeyLength, errors))
        {
            return;
        }

        errors.AddRange(_errors);

        if (_kind is null)
        {
            errors.Add($"Stage '{Key}': signing kind is not set, call Manual(...) or Automatic(...).");
        }
        else
        {
            Identifier.Validate($"Stage '{Key}': signer", _signer, RouteLimits.MaxSignerLength, errors);
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
        new(Key, _kind!.Value, _signer!, _timeout!.Value, _condition);
}

internal static class Identifier
{
    /// <summary>Checks that an identifier is non-empty, has no surrounding whitespace and fits the column.</summary>
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
