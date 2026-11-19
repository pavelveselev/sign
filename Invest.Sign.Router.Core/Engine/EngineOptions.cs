namespace Invest.Sign.Router.Core.Engine;

/// <summary>Настройки движка. Привязываются из конфигурации и проверяются хостом при запуске.</summary>
public sealed class EngineOptions
{
    /// <summary>Длительность аренды. Должна превышать самый долгий возможный шаг, иначе её может перехватить другая реплика.</summary>
    public TimeSpan LeaseDuration { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>Как часто опрашивается результат этапа в состоянии REQUESTED.</summary>
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromMinutes(1);

    /// <summary>Максимум экземпляров маршрутов, обрабатываемых за такт.</summary>
    public int BatchSize { get; set; } = 20;

    /// <summary>Максимум новых документов, забираемых за такт.</summary>
    public int DiscoveryBatchSize { get; set; } = 100;

    /// <summary>Число подряд идущих технических ошибок, после которого маршрут завершается с <c>ATTEMPTS_EXHAUSTED</c>.</summary>
    public int MaxAttempts { get; set; } = 10;

    /// <summary>Задержка после первой технической ошибки; удваивается с каждой следующей.</summary>
    public TimeSpan RetryBaseDelay { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>Верхняя граница задержки повтора.</summary>
    public TimeSpan RetryMaxDelay { get; set; } = TimeSpan.FromMinutes(30);

    /// <summary>Возвращает ошибки проверки; пусто, если настройки корректны.</summary>
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

    /// <summary>Задержка перед следующей попыткой после <paramref name="attempts"/> подряд идущих ошибок (нумерация с 1).</summary>
    public TimeSpan GetRetryDelay(int attempts)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(attempts, 1);

        // Ограничиваем показатель степени, чтобы избежать переполнения; результат всё равно ограничен максимальной задержкой.
        var factor = Math.Pow(2, Math.Min(attempts - 1, 30));
        var delay = TimeSpan.FromTicks((long)Math.Min(RetryBaseDelay.Ticks * factor, RetryMaxDelay.Ticks));
        return delay;
    }
}
