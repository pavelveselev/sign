using Invest.Sign.Router.Core.Abstractions;
using Invest.Sign.Router.Core.Discovery;

namespace Invest.Sign.Router.Tests.Discovery;

public class WatermarkIncomingDocumentSourceTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 1, 0, 0, 0, TimeSpan.Zero);

    private readonly FakeFeed _feed = new();
    private readonly FakeKnown _known = new();

    [Fact]
    public async Task Without_known_documents_reads_the_whole_feed_from_the_start()
    {
        _feed.Add("a", T0);
        _feed.Add("b", T0.AddMinutes(1));

        var result = await Source(TimeSpan.FromDays(1)).GetNewAsync(10, default);

        Assert.Equal(["a", "b"], result.Select(d => d.DocId));
        Assert.Null(Assert.Single(_feed.Calls).Since);
    }

    [Fact]
    public async Task Reads_from_watermark_minus_lookback_and_skips_known_documents()
    {
        _feed.Add("old", T0.AddDays(-5));
        _feed.Add("late", T0.AddHours(-2));   // стал виден позже, чем "known"
        _feed.Add("known", T0);
        _feed.Add("new", T0.AddMinutes(1));
        _known.Add("old", T0.AddDays(-5));
        _known.Add("known", T0);

        var result = await Source(TimeSpan.FromHours(3)).GetNewAsync(10, default);

        Assert.Equal(["late", "new"], result.Select(d => d.DocId));
        Assert.Equal(T0.AddHours(-3), _feed.Calls[0].Since);
    }

    [Fact]
    public async Task Pages_through_known_documents_until_the_limit_is_filled()
    {
        for (var i = 0; i < 7; i++)
        {
            var id = $"k{i}";
            _feed.Add(id, T0.AddSeconds(i));
            _known.Add(id, T0.AddSeconds(i));
        }

        // Одинаковое время: позиция в ленте различает строки по doc_id.
        _feed.Add("n1", T0.AddSeconds(10));
        _feed.Add("n2", T0.AddSeconds(10));
        _feed.Add("n3", T0.AddSeconds(10));

        var result = await Source(TimeSpan.FromDays(1)).GetNewAsync(2, default);

        Assert.Equal(["n1", "n2"], result.Select(d => d.DocId));
        Assert.True(_feed.Calls.Count > 1);
    }

    [Fact]
    public async Task Stops_when_the_feed_is_exhausted()
    {
        _feed.Add("k", T0);
        _known.Add("k", T0);

        var result = await Source(TimeSpan.Zero).GetNewAsync(1, default);

        Assert.Empty(result);
    }

    private WatermarkIncomingDocumentSource Source(TimeSpan lookback) => new(_feed, _known, lookback);

    private sealed class FakeFeed : IIncomingDocumentFeed
    {
        private readonly List<IncomingDocument> _rows = [];

        public List<(DateTimeOffset? Since, FeedPosition? After)> Calls { get; } = [];

        public void Add(string docId, DateTimeOffset createdAt) =>
            _rows.Add(new IncomingDocument(docId, "document", null, null, createdAt));

        public Task<IReadOnlyList<IncomingDocument>> ReadAsync(
            DateTimeOffset? since, FeedPosition? after, int limit, CancellationToken cancellationToken)
        {
            Calls.Add((since, after));
            IReadOnlyList<IncomingDocument> page = _rows
                .Where(r => since is null || r.CreatedAt >= since)
                .Where(r => after is null
                    || r.CreatedAt > after.CreatedAt
                    || (r.CreatedAt == after.CreatedAt && string.CompareOrdinal(r.DocId, after.DocId) > 0))
                .OrderBy(r => r.CreatedAt).ThenBy(r => r.DocId, StringComparer.Ordinal)
                .Take(limit)
                .ToList();
            return Task.FromResult(page);
        }
    }

    private sealed class FakeKnown : IKnownDocuments
    {
        private readonly Dictionary<string, DateTimeOffset> _known = [];

        public void Add(string docId, DateTimeOffset createdAt) => _known[docId] = createdAt;

        public Task<DateTimeOffset?> GetWatermarkAsync(CancellationToken cancellationToken) =>
            Task.FromResult(_known.Count == 0 ? (DateTimeOffset?)null : _known.Values.Max());

        public Task<IReadOnlySet<string>> FindKnownAsync(
            IReadOnlyCollection<string> docIds, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlySet<string>>(docIds.Where(_known.ContainsKey).ToHashSet());
    }
}
