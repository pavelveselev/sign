namespace Invest.Sign.Router.Core.Model;

/// <summary>Коды причин в <c>status_reason</c> для терминальных статусов маршрута, устанавливаемых самим сервисом.</summary>
public static class StatusReasons
{
    /// <summary>Для типа документа не зарегистрирован маршрут.</summary>
    public const string UnknownDocType = "UNKNOWN_DOC_TYPE";

    /// <summary>Версия маршрута, зафиксированная в экземпляре, больше отсутствует в реестре.</summary>
    public const string RouteNotFound = "ROUTE_NOT_FOUND";

    /// <summary>Хеш или размер подписанного файла не совпадает со значениями, которые вернул обработчик этапа.</summary>
    public const string ArtifactMismatch = "ARTIFACT_MISMATCH";

    /// <summary>Технические ошибки превысили настроенное число попыток.</summary>
    public const string AttemptsExhausted = "ATTEMPTS_EXHAUSTED";

    /// <summary>Условие входа в этап выбросило исключение, например обязательный атрибут отсутствует или некорректен. Не повторяется.</summary>
    public const string ConditionError = "CONDITION_ERROR";

    /// <summary>Обработчик этапа вернул бизнес-ошибку без повтора. Подробности — в причине этапа.</summary>
    public const string StageError = "STAGE_ERROR";
}
