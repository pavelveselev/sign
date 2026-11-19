namespace Invest.Sign.Router.Core.Abstractions;

/// <summary>Источник новых документов (открытый вопрос 2: представление входящих документов во внешней схеме).</summary>
public interface IIncomingDocumentSource
{
    /// <summary>
    /// Возвращает документы, у которых ещё нет экземпляра маршрута, начиная с самых старых, не более <paramref name="limit"/>.
    /// Другая реплика может одновременно вставить тот же документ; движок это обрабатывает.
    /// </summary>
    Task<IReadOnlyList<IncomingDocument>> GetNewAsync(int limit, CancellationToken cancellationToken);
}

/// <summary>Строка представления входящих документов. Строки никогда не меняются и не исчезают.</summary>
public sealed record IncomingDocument(
    string DocId,
    string DocType,
    string? Attributes,
    string? ContentHash,
    DateTimeOffset CreatedAt);
