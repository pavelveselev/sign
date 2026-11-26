namespace Invest.Sign.Router.PosDb;

/// <summary>
/// Текущее состояние документа (<c>sign_document</c>): одна строка на документ. Объединяет экземпляр маршрута,
/// аренду и текущий этап — этапы последовательные, поэтому активен не более чем один. Прошлые этапы остаются
/// в истории (<see cref="DocumentStatusHistoryEntity"/>).
/// </summary>
public sealed class DocumentEntity
{
    public long Id { get; set; }

    /// <summary>Идентификатор документа во внешней системе (<c>doc_id</c> вьюхи), уникальный.</summary>
    public string ExternalDocId { get; set; } = "";

    public string DocType { get; set; } = "";

    /// <summary>JSON атрибутов документа как есть, без проверки: некорректный JSON обрабатывает движок.</summary>
    public string? Attributes { get; set; }

    public string? ContentHash { get; set; }

    /// <summary><c>created_at</c> строки вьюхи входящих документов; основа водяного знака обнаружения.</summary>
    public DateTimeOffset? SourceCreatedAt { get; set; }

    public string? RouteCode { get; set; }

    public int? RouteVersion { get; set; }

    /// <summary><c>ACTIVE</c>, <c>COMPLETED</c>, <c>REJECTED</c>, <c>EXPIRED</c>, <c>CANCELLED</c>, <c>FAILED</c>.</summary>
    public string Status { get; set; } = "";

    public string? StatusReason { get; set; }

    public int Attempts { get; set; }

    public DateTimeOffset NextAttemptAt { get; set; }

    public string? LockedBy { get; set; }

    public DateTimeOffset? LockedUntil { get; set; }

    /// <summary>Идентификатор текущего этапа из последовательности <c>sign_stage_id_seq</c>; null до первого этапа.</summary>
    public long? CurrentStageId { get; set; }

    public string? StageKey { get; set; }

    /// <summary><c>PENDING</c>, <c>REQUESTED</c>, <c>DONE</c>, <c>REJECTED</c>, <c>EXPIRED</c>, <c>FAILED</c>.</summary>
    public string? StageState { get; set; }

    /// <summary>Идентификатор запроса текущего этапа, генерирует сервис.</summary>
    public string? RequestId { get; set; }

    public DateTimeOffset? StageRequestedAt { get; set; }

    public DateTimeOffset? StageDeadlineAt { get; set; }

    public DateTimeOffset? StageFinishedAt { get; set; }

    public string? StageReason { get; set; }

    /// <summary>Собственные данные обработчика текущего этапа.</summary>
    public string? StageData { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>
/// История изменения статусов (<c>sign_document_status_history</c>), только вставка. Каждая запись — событие движка
/// и снимок статусов документа и этапа сразу после него, в той же транзакции, что и сам переход.
/// </summary>
public sealed class DocumentStatusHistoryEntity
{
    public long Id { get; set; }

    public long DocumentId { get; set; }

    /// <summary>Тип события (<c>AuditEventTypes</c>): <c>ROUTE_CREATED</c>, <c>STAGE_DONE</c> и т. д.</summary>
    public string EventType { get; set; } = "";

    public string Status { get; set; } = "";

    public string? StatusReason { get; set; }

    public long? StageId { get; set; }

    public string? StageKey { get; set; }

    public string? StageState { get; set; }

    /// <summary>Подробности события, JSON.</summary>
    public string? Payload { get; set; }

    /// <summary>Идентификатор реплики, выполнившей переход.</summary>
    public string Actor { get; set; } = "";

    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>
/// Подпись документа (<c>sign_signature</c>), только вставка. Отдельная таблица: у документа может быть несколько
/// подписей (по одной на этап), а двоичное содержимое не должно утяжелять строку, которую опрашивает движок.
/// </summary>
public sealed class SignatureEntity
{
    public long Id { get; set; }

    public long DocumentId { get; set; }

    public long StageId { get; set; }

    public string StageKey { get; set; } = "";

    /// <summary>Имя файла подписи или путь на общем ресурсе, откуда она прочитана.</summary>
    public string SourceRef { get; set; } = "";

    public byte[] Content { get; set; } = [];

    /// <summary>SHA-256 содержимого, hex в нижнем регистре.</summary>
    public string Sha256 { get; set; } = "";

    public long SizeBytes { get; set; }

    public DateTimeOffset StoredAt { get; set; }
}
