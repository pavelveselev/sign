namespace Invest.Sign.Router;

/// <summary>Настройки хоста: цикл тактов, остановка, идентификатор реплики.</summary>
public sealed class RouterOptions
{
    public const string SectionName = "Router";

    /// <summary>Секция с настройками движка (<see cref="Core.Engine.EngineOptions"/>).</summary>
    public const string EngineSectionName = "Engine";

    /// <summary>Пауза между тактами, если предыдущий такт не выбрал пачку целиком.</summary>
    public TimeSpan TickInterval { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Сколько ждать завершения текущего шага после сигнала остановки. В Kubernetes
    /// <c>terminationGracePeriodSeconds</c> должен быть больше.
    /// </summary>
    public TimeSpan ShutdownTimeout { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>
    /// Основа идентификатора реплики. Если пусто — переменная окружения <c>POD_NAME</c>, затем имя машины.
    /// К основе всегда добавляется случайный суффикс.
    /// </summary>
    public string? ReplicaName { get; set; }

    /// <summary>
    /// Насколько раньше водяного знака (самое позднее <c>created_at</c> известного документа) перечитывается вьюха
    /// входящих документов. Страхует от строк, ставших видимыми с опозданием.
    /// </summary>
    public TimeSpan DiscoveryLookback { get; set; } = TimeSpan.FromDays(1);

    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();
        if (DiscoveryLookback < TimeSpan.Zero)
        {
            errors.Add($"{nameof(DiscoveryLookback)} must not be negative.");
        }

        if (TickInterval <= TimeSpan.Zero)
        {
            errors.Add($"{nameof(TickInterval)} must be positive.");
        }

        if (ShutdownTimeout <= TimeSpan.Zero)
        {
            errors.Add($"{nameof(ShutdownTimeout)} must be positive.");
        }

        if (ReplicaName is { Length: > ReplicaIdentity.MaxBaseLength })
        {
            errors.Add($"{nameof(ReplicaName)} must be at most {ReplicaIdentity.MaxBaseLength} characters.");
        }

        return errors;
    }
}
