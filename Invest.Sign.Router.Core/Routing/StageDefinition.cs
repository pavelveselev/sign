using Invest.Sign.Router.Core.Model;

namespace Invest.Sign.Router.Core.Routing;

/// <summary>An immutable, validated stage of a route definition.</summary>
public sealed class StageDefinition
{
    internal StageDefinition(
        string key,
        SigningKind kind,
        string signer,
        TimeSpan timeout,
        Func<DocumentContext, bool>? condition)
    {
        Key = key;
        Kind = kind;
        Signer = signer;
        Timeout = timeout;
        Condition = condition;
    }

    /// <summary>Stage key, unique within the route. Stored in <c>stage_key</c>.</summary>
    public string Key { get; }

    public SigningKind Kind { get; }

    /// <summary>Signer identifier passed to the signing software as <c>p_signer</c>.</summary>
    public string Signer { get; }

    /// <summary>Time allowed for the signing request, counted from the moment it is created.</summary>
    public TimeSpan Timeout { get; }

    /// <summary>Entry condition, null means the stage always applies.</summary>
    public Func<DocumentContext, bool>? Condition { get; }

    public bool IsConditional => Condition is not null;

    /// <summary>
    /// Evaluates the entry condition against document attributes.
    /// Exceptions from the condition (e.g. <see cref="DocumentAttributeException"/>) propagate to the caller.
    /// </summary>
    public bool AppliesTo(DocumentContext document)
    {
        ArgumentNullException.ThrowIfNull(document);
        return Condition is null || Condition(document);
    }

    public override string ToString() => Key;
}
