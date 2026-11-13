namespace Invest.Sign.Router.Core.Engine;

public enum StepOutcome
{
    /// <summary>The instance was taken by another replica or is no longer due.</summary>
    NotAcquired,

    /// <summary>One transition (or a poll without change) was committed.</summary>
    Advanced,

    /// <summary>The lease was lost during the step; nothing was written.</summary>
    LeaseLost,

    /// <summary>A technical failure was recorded; a retry is scheduled or attempts are exhausted.</summary>
    TechnicalFailure,

    /// <summary>A technical failure happened and could not be recorded; the lease will expire on its own.</summary>
    FailureNotRecorded,
}

/// <summary>Result of processing one route instance. <see cref="Error"/> is set for failures.</summary>
public sealed record StepResult(long RouteId, StepOutcome Outcome, Exception? Error = null);

/// <summary>
/// Result of the discovery phase. <see cref="SourceError"/> is set when the incoming documents could not be read;
/// <see cref="Errors"/> lists documents that failed to be inserted.
/// </summary>
public sealed record DiscoveryResult(
    int Created,
    int AlreadyExisting,
    IReadOnlyList<(string DocId, Exception Error)> Errors,
    Exception? SourceError = null);

/// <summary>Result of one tick.</summary>
public sealed record TickResult(DiscoveryResult Discovery, IReadOnlyList<StepResult> Steps);
