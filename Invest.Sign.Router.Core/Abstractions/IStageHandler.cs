using Invest.Sign.Router.Core.Model;
using Invest.Sign.Router.Core.Routing;

namespace Invest.Sign.Router.Core.Abstractions;

/// <summary>
/// Действие этапа: что этап делает и как узнаёт результат. Этап выбирает обработчик в определении маршрута
/// (<see cref="StageBuilder.Handler{THandler}"/>); движок отвечает за аренды, переходы состояний, сроки, повторы и аудит.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="StartAsync"/>, <see cref="CheckAsync"/> и <see cref="CancelAsync"/> выполняются <b>вне</b> транзакции БД:
/// в них можно копировать файлы и вызывать HTTP. Если процесс упадёт после побочного эффекта, но до фиксации перехода,
/// шаг повторится, поэтому они обязаны быть идемпотентны по <see cref="StageContext.RequestId"/>.
/// </para>
/// <para>
/// <see cref="OnTransitionAsync"/> выполняется <b>внутри</b> транзакции шага, после проверки аренды и до записи состояния этапа:
/// здесь пишутся собственные данные обработчика в БД, они фиксируются или откатываются вместе с переходом.
/// </para>
/// <para>
/// Любое исключение — техническая ошибка: шаг повторяется с экспоненциальной задержкой. Бизнес-исходы возвращаются
/// как <see cref="StageOutcome.Rejected"/> или <see cref="StageOutcome.Failed"/>.
/// Каждый вызов должен укладываться в <c>Engine:LeaseDuration</c>.
/// </para>
/// </remarks>
public interface IStageHandler
{
    /// <summary>
    /// Запускает этап в состоянии <c>PENDING</c>. <see cref="StageOutcome.Waiting"/> переводит этап в <c>REQUESTED</c>
    /// с опросом через <see cref="CheckAsync"/>; остальные исходы завершают этап сразу.
    /// </summary>
    Task<StageOutcome> StartAsync(StageContext stage, CancellationToken cancellationToken);

    /// <summary>Опрашивает этап в состоянии <c>REQUESTED</c>. Вызывается раз в <c>Engine:PollInterval</c> до срока этапа.</summary>
    Task<StageOutcome> CheckAsync(StageContext stage, CancellationToken cancellationToken);

    /// <summary>Вызывается, когда срок этапа истёк, перед переводом этапа в <c>EXPIRED</c>. Должен быть идемпотентен.</summary>
    Task CancelAsync(StageContext stage, CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>
    /// Вызывается в транзакции каждого перехода этапа, который инициировал обработчик или срок этапа:
    /// <c>PENDING → REQUESTED</c>, переход в <c>DONE</c>, <c>REJECTED</c>, <c>FAILED</c> или <c>EXPIRED</c>.
    /// Не вызывается при завершении маршрута по причинам самого движка (исчерпаны попытки, маршрут не найден).
    /// </summary>
    Task OnTransitionAsync(StageTransition transition, IUnitOfWork unitOfWork, CancellationToken cancellationToken) =>
        Task.CompletedTask;

    /// <summary>
    /// Проверяет настройки этапа при запуске движка. Пустой список — настройки подходят.
    /// Ошибка останавливает запуск, а не всплывает на первом документе.
    /// </summary>
    IReadOnlyList<string> ValidateSettings(object? settings) => [];
}

/// <summary>Находит обработчик по типу, указанному в определении этапа.</summary>
public interface IStageHandlerResolver
{
    /// <summary>Обработчик для <paramref name="handlerType"/>; null, если он не зарегистрирован.</summary>
    IStageHandler? Find(Type handlerType);
}

/// <summary>Простой реестр обработчиков в памяти: по умолчанию ключ — фактический тип экземпляра.</summary>
public sealed class StageHandlerRegistry : IStageHandlerResolver
{
    private readonly Dictionary<Type, IStageHandler> _handlers = [];

    public StageHandlerRegistry(params IEnumerable<IStageHandler> handlers)
    {
        foreach (var handler in handlers)
        {
            Add(handler.GetType(), handler);
        }
    }

    /// <summary>Регистрирует <paramref name="handler"/> под типом <paramref name="handlerType"/>, например обёртку в тестах.</summary>
    public StageHandlerRegistry Add(Type handlerType, IStageHandler handler)
    {
        ArgumentNullException.ThrowIfNull(handlerType);
        ArgumentNullException.ThrowIfNull(handler);
        _handlers[handlerType] = handler;
        return this;
    }

    public IStageHandler? Find(Type handlerType) => _handlers.GetValueOrDefault(handlerType);
}

/// <summary>Всё, что обработчику известно об этапе.</summary>
/// <param name="RouteId">Идентификатор экземпляра маршрута.</param>
/// <param name="StageId">Идентификатор экземпляра этапа.</param>
/// <param name="StageKey">Ключ этапа из определения маршрута.</param>
/// <param name="RequestId">Уникальный идентификатор этапа, созданный сервисом; используйте как ключ идемпотентности.</param>
/// <param name="Document">Документ: идентификатор, тип, атрибуты.</param>
/// <param name="Settings">Настройки этапа из определения маршрута.</param>
/// <param name="Data">Данные, которые обработчик вернул ранее (<c>SGN_STAGE_INSTANCE.data</c>); null до первого сохранения.</param>
public sealed record StageContext(
    long RouteId,
    long StageId,
    string StageKey,
    string RequestId,
    DocumentContext Document,
    object? Settings,
    string? Data)
{
    /// <summary>Настройки этапа ожидаемого типа.</summary>
    /// <exception cref="InvalidOperationException">Настройки отсутствуют или имеют другой тип.</exception>
    public T GetSettings<T>() where T : class =>
        Settings as T ?? throw new InvalidOperationException(
            $"Stage '{StageKey}' expects settings of type {typeof(T).Name}, got {Settings?.GetType().Name ?? "null"}.");
}

/// <summary>Результат вызова обработчика.</summary>
public abstract record StageOutcome
{
    private StageOutcome()
    {
    }

    /// <summary>Этап ещё не завершён: перейти или остаться в <c>REQUESTED</c> и опросить позже.</summary>
    /// <param name="Data">Новые данные обработчика; null оставляет сохранённые.</param>
    public sealed record Waiting(string? Data = null) : StageOutcome;

    /// <summary>Этап выполнен.</summary>
    /// <param name="Data">Новые данные обработчика; null оставляет сохранённые.</param>
    /// <param name="File">Файл результата: движок сверит хеш и размер и сохранит его в <c>SGN_SIGNED_ARTIFACT</c>.</param>
    /// <param name="CompletedAt">Когда этап выполнен на внешней стороне, только для аудита.</param>
    public sealed record Done(string? Data = null, StageFile? File = null, DateTimeOffset? CompletedAt = null) : StageOutcome;

    /// <summary>Отказ: маршрут завершается со статусом <c>REJECTED</c>.</summary>
    public sealed record Rejected(string? Reason) : StageOutcome;

    /// <summary>Бизнес-ошибка без повтора: маршрут завершается со статусом <c>FAILED</c>.</summary>
    /// <param name="Reason">Подробности, сохраняются в <c>SGN_STAGE_INSTANCE.reason</c>.</param>
    /// <param name="RouteReason">Код причины для <c>SGN_ROUTE_INSTANCE.status_reason</c>.</param>
    public sealed record Failed(string? Reason, string RouteReason = StatusReasons.StageError) : StageOutcome;
}

/// <summary>Файл результата этапа.</summary>
/// <param name="FileRef">Путь на общем ресурсе (<see cref="ISignedFileSource"/>) или, если задан <paramref name="Content"/>, имя файла для аудита.</param>
/// <param name="Hash">Ожидаемый SHA-256 (hex); движок сверяет его с фактическим.</param>
/// <param name="Size">Ожидаемый размер в байтах.</param>
/// <param name="Content">
/// Содержимое, уже прочитанное обработчиком. Если задано, движок не читает файл через <see cref="ISignedFileSource"/>.
/// </param>
public sealed record StageFile(string FileRef, string? Hash = null, long? Size = null, byte[]? Content = null);

/// <summary>Переход этапа, передаваемый в <see cref="IStageHandler.OnTransitionAsync"/>.</summary>
/// <param name="Stage">Этап до перехода.</param>
/// <param name="From">Состояние до перехода: <c>PENDING</c> или <c>REQUESTED</c>.</param>
/// <param name="To">Состояние после перехода.</param>
/// <param name="Outcome">Исход обработчика, вызвавший переход; null для истечения срока.</param>
/// <param name="Reason">Причина, записываемая в этап.</param>
public sealed record StageTransition(
    StageContext Stage,
    StageState From,
    StageState To,
    StageOutcome? Outcome,
    string? Reason);
