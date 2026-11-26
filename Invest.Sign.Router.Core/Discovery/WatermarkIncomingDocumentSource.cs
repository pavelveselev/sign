using Invest.Sign.Router.Core.Abstractions;

namespace Invest.Sign.Router.Core.Discovery;

/// <summary>
/// Лента входящих документов во внешней системе (вьюха Oracle), упорядоченная по <c>(created_at, doc_id)</c>.
/// Строки никогда не меняются и не исчезают.
/// </summary>
public interface IIncomingDocumentFeed
{
    /// <summary>
    /// Документы с <c>created_at &gt;= since</c> (все, если <paramref name="since"/> null), строго после позиции
    /// <paramref name="after"/>, по возрастанию <c>(created_at, doc_id)</c>, не более <paramref name="limit"/>.
    /// </summary>
    Task<IReadOnlyList<IncomingDocument>> ReadAsync(
        DateTimeOffset? since, FeedPosition? after, int limit, CancellationToken cancellationToken);
}

/// <summary>Позиция в ленте: последняя прочитанная строка.</summary>
public sealed record FeedPosition(DateTimeOffset CreatedAt, string DocId);

/// <summary>Документы, уже известные сервису (у них есть экземпляр маршрута).</summary>
public interface IKnownDocuments
{
    /// <summary>Самое позднее <c>created_at</c> из ленты среди известных документов; null, если документов нет.</summary>
    Task<DateTimeOffset?> GetWatermarkAsync(CancellationToken cancellationToken);

    /// <summary>Те из <paramref name="docIds"/>, которые уже известны сервису.</summary>
    Task<IReadOnlySet<string>> FindKnownAsync(IReadOnlyCollection<string> docIds, CancellationToken cancellationToken);
}

/// <summary>
/// Новые документы, когда лента и состояние сервиса живут в разных базах и анти-соединение невозможно.
/// Лента читается начиная с водяного знака (самое позднее <c>created_at</c> известного документа) минус
/// <see cref="Lookback"/>; уже известные документы отфильтровываются.
/// </summary>
/// <remarks>
/// Окно <see cref="Lookback"/> страхует от строк, которые стали видны позже строк с бо́льшим <c>created_at</c>
/// (например, транзакция внешней стороны зафиксирована с опозданием). Документ, ставший видимым позже, чем через
/// <see cref="Lookback"/> после водяного знака, обнаружен не будет.
/// </remarks>
public sealed class WatermarkIncomingDocumentSource : IIncomingDocumentSource
{
    private readonly IIncomingDocumentFeed _feed;
    private readonly IKnownDocuments _known;

    public WatermarkIncomingDocumentSource(IIncomingDocumentFeed feed, IKnownDocuments known, TimeSpan lookback)
    {
        if (lookback < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(lookback), lookback, "Lookback must not be negative.");
        }

        _feed = feed;
        _known = known;
        Lookback = lookback;
    }

    public TimeSpan Lookback { get; }

    public async Task<IReadOnlyList<IncomingDocument>> GetNewAsync(int limit, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);

        var watermark = await _known.GetWatermarkAsync(cancellationToken);
        var since = watermark - Lookback;

        var result = new List<IncomingDocument>(limit);
        FeedPosition? after = null;
        while (result.Count < limit)
        {
            // В окне Lookback большинство строк уже известны, поэтому лента читается постранично до набора лимита.
            var page = await _feed.ReadAsync(since, after, limit, cancellationToken);
            if (page.Count == 0)
            {
                break;
            }

            var known = await _known.FindKnownAsync(page.Select(d => d.DocId).ToList(), cancellationToken);
            result.AddRange(page.Where(d => !known.Contains(d.DocId)).Take(limit - result.Count));

            if (page.Count < limit)
            {
                break;
            }

            var last = page[^1];
            after = new FeedPosition(last.CreatedAt, last.DocId);
        }

        return result;
    }
}
