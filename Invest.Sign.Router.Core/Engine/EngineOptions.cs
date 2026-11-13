namespace Invest.Sign.Router.Core.Engine;

/// <summary>Engine settings. Bound from configuration and validated at startup by the host.</summary>
public sealed class EngineOptions
{
    /// <summary>Lease length. Must exceed the longest possible step, otherwise another replica may take over.</summary>
    public TimeSpan LeaseDuration { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>How often a requested stage is polled for its result.</summary>
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromMinutes(1);

    /// <summary>Max route instances processed per tick.</summary>
    public int BatchSize { get; set; } = 20;

    /// <summary>Max new documents picked up per tick.</summary>
    public int DiscoveryBatchSize { get; set; } = 100;

    /// <summary>Consecutive technical failures after which the route fails with <c>ATTEMPTS_EXHAUSTED</c>.</summary>
    public int MaxAttempts { get; set; } = 10;

    /// <summary>Delay after the first technical failure; doubles with each next one.</summary>
    public TimeSpan RetryBaseDelay { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>Upper bound of the retry delay.</summary>
    public TimeSpan RetryMaxDelay { get; set; } = TimeSpan.FromMinutes(30);

    /// <summary>Returns validation errors, empty when the options are valid.</summary>
    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();
        if (LeaseDuration <= TimeSpan.Zero)
        {
            errors.Add($"{nameof(LeaseDuration)} must be positive.");
        }

        if (PollInterval <= TimeSpan.Zero)
        {
            errors.Add($"{nameof(PollInterval)} must be positive.");
        }

        if (BatchSize < 1)
        {
            errors.Add($"{nameof(BatchSize)} must be at least 1.");
        }

        if (DiscoveryBatchSize < 1)
        {
            errors.Add($"{nameof(DiscoveryBatchSize)} must be at least 1.");
        }

        if (MaxAttempts < 1)
        {
            errors.Add($"{nameof(MaxAttempts)} must be at least 1.");
        }

        if (RetryBaseDelay <= TimeSpan.Zero)
        {
            errors.Add($"{nameof(RetryBaseDelay)} must be positive.");
        }

        if (RetryMaxDelay < RetryBaseDelay)
        {
            errors.Add($"{nameof(RetryMaxDelay)} must not be less than {nameof(RetryBaseDelay)}.");
        }

        return errors;
    }

    /// <summary>Delay before the next attempt after <paramref name="attempts"/> consecutive failures (1-based).</summary>
    public TimeSpan GetRetryDelay(int attempts)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(attempts, 1);

        // Cap the exponent to avoid overflow; the max delay bounds the result anyway.
        var factor = Math.Pow(2, Math.Min(attempts - 1, 30));
        var delay = TimeSpan.FromTicks((long)Math.Min(RetryBaseDelay.Ticks * factor, RetryMaxDelay.Ticks));
        return delay;
    }
}
