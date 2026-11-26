using Invest.Sign.Router.PosDb;

namespace Invest.Sign.Router;

/// <summary>Точка входа для чтения статусов документов из базы состояния.</summary>
public sealed class DocumentStatusService(DocumentStatusReader reader)
{
    /// <summary>
    /// Текущий статус документа и история изменения его статусов по возрастанию времени.
    /// </summary>
    /// <param name="docId">Идентификатор документа во внешней системе (<c>doc_id</c> вьюхи входящих документов).</param>
    /// <returns>Null, если сервис ещё не обнаружил документ с таким идентификатором.</returns>
    public Task<DocumentStatusHistory?> GetStatusHistoryAsync(string docId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(docId);
        return reader.GetAsync(docId, cancellationToken);
    }
}
