namespace Invest.Sign.Router.Core.Abstractions;

/// <summary>Source of new documents (open question 2: a view of incoming documents in the external schema).</summary>
public interface IIncomingDocumentSource
{
    /// <summary>
    /// Returns documents that have no route instance yet, oldest first, at most <paramref name="limit"/>.
    /// Another replica may insert the same document concurrently; the engine handles that.
    /// </summary>
    Task<IReadOnlyList<IncomingDocument>> GetNewAsync(int limit, CancellationToken cancellationToken);
}

/// <summary>A row of the incoming documents view. Rows never change or disappear.</summary>
public sealed record IncomingDocument(
    string DocId,
    string DocType,
    string? Attributes,
    string? ContentHash,
    DateTimeOffset CreatedAt);
