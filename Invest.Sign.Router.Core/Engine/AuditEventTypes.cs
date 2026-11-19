namespace Invest.Sign.Router.Core.Engine;

/// <summary>Значения <c>SGN_AUDIT_EVENT.event_type</c>.</summary>
public static class AuditEventTypes
{
    public const string RouteCreated = "ROUTE_CREATED";

    /// <summary>Маршрут достиг терминального статуса. Данные: статус, причина.</summary>
    public const string RouteFinished = "ROUTE_FINISHED";

    public const string StageSkipped = "STAGE_SKIPPED";
    public const string StageEntered = "STAGE_ENTERED";
    public const string StageRequested = "STAGE_REQUESTED";
    public const string StageDone = "STAGE_DONE";
    public const string StageRejected = "STAGE_REJECTED";
    public const string StageExpired = "STAGE_EXPIRED";
    public const string StageFailed = "STAGE_FAILED";

    /// <summary>Зафиксирована техническая ошибка и запланирован повтор.</summary>
    public const string TechnicalFailure = "TECHNICAL_FAILURE";
}
