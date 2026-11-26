using Invest.Sign.Router.Core.Discovery;
using Microsoft.EntityFrameworkCore;

namespace Invest.Sign.Router.PosDb;

/// <summary>Документы, уже заведённые в <c>sign_document</c>, для обнаружения новых документов по водяному знаку.</summary>
public sealed class PostgresKnownDocuments(IDocumentDbContextFactory contexts) : IKnownDocuments
{
    public async Task<DateTimeOffset?> GetWatermarkAsync(CancellationToken cancellationToken)
    {
        await using var db = contexts.CreateDbContext();
        return await db.Documents.MaxAsync(d => d.SourceCreatedAt, cancellationToken);
    }

    public async Task<IReadOnlySet<string>> FindKnownAsync(
        IReadOnlyCollection<string> docIds, CancellationToken cancellationToken)
    {
        if (docIds.Count == 0)
        {
            return new HashSet<string>();
        }

        await using var db = contexts.CreateDbContext();
        var known = await db.Documents
            .Where(d => docIds.Contains(d.ExternalDocId))
            .Select(d => d.ExternalDocId)
            .ToListAsync(cancellationToken);
        return known.ToHashSet(StringComparer.Ordinal);
    }
}
