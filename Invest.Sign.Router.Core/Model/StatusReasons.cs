namespace Invest.Sign.Router.Core.Model;

/// <summary>Reason codes stored in <c>status_reason</c> for terminal route statuses set by the service itself.</summary>
public static class StatusReasons
{
    /// <summary>No route is registered for the document type.</summary>
    public const string UnknownDocType = "UNKNOWN_DOC_TYPE";

    /// <summary>The route version fixed on the instance is no longer in the registry.</summary>
    public const string RouteNotFound = "ROUTE_NOT_FOUND";

    /// <summary>Signed file hash or size does not match the values reported by the signing software.</summary>
    public const string ArtifactMismatch = "ARTIFACT_MISMATCH";

    /// <summary>Technical failures exceeded the configured number of attempts.</summary>
    public const string AttemptsExhausted = "ATTEMPTS_EXHAUSTED";

    /// <summary>A stage entry condition threw, e.g. a required attribute is missing or malformed. Not retried.</summary>
    public const string ConditionError = "CONDITION_ERROR";

    /// <summary>The signing software reported <c>ERROR</c> for a request. Details are in the stage reason.</summary>
    public const string SigningError = "SIGNING_ERROR";
}
