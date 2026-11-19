using Invest.Sign.Router.Core.Model;

namespace Invest.Sign.Router.Core.Routing;

/// <summary>Неизменяемый проверенный этап определения маршрута.</summary>
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

    /// <summary>Ключ этапа, уникальный в пределах маршрута. Хранится в <c>stage_key</c>.</summary>
    public string Key { get; }

    public SigningKind Kind { get; }

    /// <summary>Идентификатор подписанта, передаваемый в ПО подписания как <c>p_signer</c>.</summary>
    public string Signer { get; }

    /// <summary>Время, отведённое на запрос на подписание, отсчитываемое с момента его создания.</summary>
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
