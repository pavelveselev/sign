namespace Invest.Sign.Router.Core.Engine;

public enum StepOutcome
{
    /// <summary>Экземпляр захвачен другой репликой или больше не готов к обработке.</summary>
    NotAcquired,

    /// <summary>Зафиксирован один переход (или опрос без изменений).</summary>
    Advanced,

    /// <summary>Аренда потеряна во время шага; ничего не записано.</summary>
    LeaseLost,

    /// <summary>Зафиксирована техническая ошибка; запланирован повтор или попытки исчерпаны.</summary>
    TechnicalFailure,

    /// <summary>Произошла техническая ошибка, и её не удалось зафиксировать; аренда истечёт сама.</summary>
    FailureNotRecorded,
}

/// <summary>Результат обработки одного экземпляра маршрута. При ошибках заполняется <see cref="Error"/>.</summary>
public sealed record StepResult(long RouteId, StepOutcome Outcome, Exception? Error = null);

/// <summary>
/// Результат фазы обнаружения. <see cref="SourceError"/> заполняется, если входящие документы не удалось прочитать;
/// <see cref="Errors"/> перечисляет документы, которые не удалось вставить.
/// </summary>
public sealed record DiscoveryResult(
    int Created,
    int AlreadyExisting,
    IReadOnlyList<(string DocId, Exception Error)> Errors,
    Exception? SourceError = null);

/// <summary>Результат одного такта.</summary>
public sealed record TickResult(DiscoveryResult Discovery, IReadOnlyList<StepResult> Steps);
