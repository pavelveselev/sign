using Invest.Sign.Router.Core.Abstractions;

namespace Invest.Sign.Router.Tests.Fakes;

/// <summary>Представление входящих документов с анти-соединением по <see cref="InMemoryDatabase.Routes"/>.</summary>
public sealed class FakeIncomingDocumentSource(InMemoryDatabase db) : IIncomingDocumentSource
{
    public List<IncomingDocument> Documents { get; } = [];

    /// <summary>
    /// Возвращает документы, даже если у них уже есть экземпляр, — так их увидела бы реплика, прочитавшая представление
    /// непосредственно перед тем, как другая реплика вставила экземпляр.
    /// </summary>
    public bool Stale { get; set; }

    public void Add(string docId, string docType, string? attributes = null, string? contentHash = null) =>
        Documents.Add(new IncomingDocument(docId, docType, attributes, contentHash, db.Now));

    public Task<IReadOnlyList<IncomingDocument>> GetNewAsync(int limit, CancellationToken cancellationToken)
    {
        IReadOnlyList<IncomingDocument> result = Documents
            .Where(d => Stale || db.Routes.Values.All(r => r.ExternalDocId != d.DocId))
            .OrderBy(d => d.CreatedAt)
            .Take(limit)
            .ToList();
        return Task.FromResult(result);
    }
}

public sealed class FakeSignedFileSource : ISignedFileSource
{
    public Dictionary<string, byte[]> Files { get; } = [];

    public Task<byte[]> ReadAsync(string fileRef, CancellationToken cancellationToken) =>
        Files.TryGetValue(fileRef, out var content)
            ? Task.FromResult(content)
            : throw new FileNotFoundException($"Signed file '{fileRef}' is not found.", fileRef);
}
