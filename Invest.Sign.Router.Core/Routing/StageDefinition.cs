using Invest.Sign.Router.Core.Abstractions;
using Invest.Sign.Router.Core.Model;

namespace Invest.Sign.Router.Core.Routing;

/// <summary>Неизменяемый проверенный этап определения маршрута.</summary>
public sealed class StageDefinition
{
    internal StageDefinition(
        string key,
        Type handlerType,
        object? settings,
        TimeSpan timeout,
        Func<DocumentContext, bool>? condition)
    {
        Key = key;
        HandlerType = handlerType;
        Settings = settings;
        Timeout = timeout;
        Condition = condition;
    }

    /// <summary>Ключ этапа, уникальный в пределах маршрута. Хранится в <c>stage_key</c>.</summary>
    public string Key { get; }

    /// <summary>Тип <see cref="IStageHandler"/>, выполняющего этап. Экземпляр находит <see cref="IStageHandlerResolver"/>.</summary>
    public Type HandlerType { get; }

    /// <summary>Настройки этапа, передаваемые обработчику в <see cref="StageContext.Settings"/>.</summary>
    public object? Settings { get; }

    /// <summary>Вид подписания для этапов подписания во внешнем ПО; иначе null.</summary>
    public SigningKind? Kind => (Settings as SigningStageSettings)?.Kind;

    /// <summary>Подписант (<c>p_signer</c>) для этапов подписания во внешнем ПО; иначе null.</summary>
    public string? Signer => (Settings as SigningStageSettings)?.Signer;

    /// <summary>
    /// Сколько этап может находиться в <c>REQUESTED</c>, отсчитывая с момента запуска.
    /// Не используется, если обработчик завершает этап сразу.
    /// </summary>
    public TimeSpan Timeout { get; }

    /// <summary>Условие входа; null означает, что этап применяется всегда.</summary>
    public Func<DocumentContext, bool>? Condition { get; }

    public bool IsConditional => Condition is not null;

    /// <summary>Вычисляет условие входа по атрибутам документа.</summary>
    /// <exception cref="StageConditionException">Условие выбросило исключение; исходное исключение — внутреннее.</exception>
    public bool AppliesTo(DocumentContext document)
    {
        ArgumentNullException.ThrowIfNull(document);

        if (Condition is null)
        {
            return true;
        }

        try
        {
            return Condition(document);
        }
        catch (Exception ex)
        {
            throw new StageConditionException(Key, ex);
        }
    }

    public override string ToString() => Key;
}
