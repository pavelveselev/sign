using System.Net;
using System.Text;
using System.Text.Json;
using Invest.Sign.Router.Core.Abstractions;
using Invest.Sign.Router.Core.Model;
using Invest.Sign.Router.Infrastructure.Stages;

namespace Invest.Sign.Router.IntegrationTests.Stages;

/// <summary>Протокол HTTP-этапа поверх подменённого обработчика сообщений; сеть не нужна.</summary>
public sealed class HttpStageHandlerTests : IDisposable
{
    private readonly FakeServer _server = new();
    private readonly HttpClient _client;
    private readonly HttpStageHandler _handler;

    public HttpStageHandlerTests()
    {
        _client = new HttpClient(_server)
        {
            BaseAddress = new HttpStageOptions { BaseAddress = "https://subscriptions.test/api" }.GetBaseUri(),
        };
        _handler = new HttpStageHandler(_client);
    }

    public void Dispose() => _client.Dispose();

    [Fact]
    public async Task Start_posts_document_with_idempotency_key()
    {
        _server.Reply(HttpStatusCode.OK, """{"status":"DONE"}""");

        var outcome = await _handler.StartAsync(Stage("""{"amount":10}"""), default);

        Assert.IsType<StageOutcome.Done>(outcome);
        var request = Assert.Single(_server.Requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("https://subscriptions.test/api/subscriptions", request.Uri);
        Assert.Equal("req-1", request.IdempotencyKey);
        using var body = JsonDocument.Parse(request.Body!);
        Assert.Equal("req-1", body.RootElement.GetProperty("requestId").GetString());
        Assert.Equal("doc-1", body.RootElement.GetProperty("docId").GetString());
        Assert.Equal("contract", body.RootElement.GetProperty("docType").GetString());
        Assert.Equal("auto-sub", body.RootElement.GetProperty("stageKey").GetString());
        Assert.Equal(10, body.RootElement.GetProperty("attributes").GetProperty("amount").GetInt32());
    }

    [Fact]
    public async Task Pending_response_keeps_external_id_and_check_uses_it()
    {
        _server.Reply(HttpStatusCode.Accepted, """{"status":"PENDING","id":"ext/42"}""");
        var waiting = Assert.IsType<StageOutcome.Waiting>(await _handler.StartAsync(Stage(), default));
        Assert.Equal("""{"id":"ext/42"}""", waiting.Data);

        _server.Reply(HttpStatusCode.OK, """{"status":"REJECTED","reason":"limit exceeded"}""");
        var outcome = await _handler.CheckAsync(Stage(data: waiting.Data), default);

        Assert.Equal("limit exceeded", Assert.IsType<StageOutcome.Rejected>(outcome).Reason);
        Assert.Equal(HttpMethod.Get, _server.Requests[^1].Method);
        Assert.Equal("https://subscriptions.test/api/subscriptions/ext%2F42", _server.Requests[^1].Uri);
    }

    [Fact]
    public async Task Check_without_external_id_uses_request_id()
    {
        _server.Reply(HttpStatusCode.OK, """{"status":"failed","reason":"invalid account"}""");

        var outcome = await _handler.CheckAsync(Stage(), default);

        Assert.Equal("invalid account", Assert.IsType<StageOutcome.Failed>(outcome).Reason);
        Assert.Equal("https://subscriptions.test/api/subscriptions/req-1", _server.Requests[^1].Uri);
    }

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.RequestTimeout)]
    public async Task Transient_status_is_a_technical_error(HttpStatusCode status)
    {
        _server.Reply(status, "");

        var ex = await Assert.ThrowsAsync<HttpRequestException>(() => _handler.StartAsync(Stage(), default));
        Assert.Equal(status, ex.StatusCode);
    }

    [Fact]
    public async Task Client_error_is_a_business_failure()
    {
        _server.Reply(HttpStatusCode.BadRequest, "account is closed");

        var outcome = await _handler.StartAsync(Stage(), default);

        Assert.Equal("HTTP 400: account is closed", Assert.IsType<StageOutcome.Failed>(outcome).Reason);
    }

    [Theory]
    [InlineData("""{"status":"UNKNOWN"}""")]
    [InlineData("not json")]
    public async Task Unexpected_response_is_a_technical_error(string body)
    {
        _server.Reply(HttpStatusCode.OK, body);

        await Assert.ThrowsAsync<InvalidOperationException>(() => _handler.StartAsync(Stage(), default));
    }

    [Theory]
    [InlineData(HttpStatusCode.NoContent)]
    [InlineData(HttpStatusCode.NotFound)]
    public async Task Cancel_deletes_resource_and_tolerates_missing_one(HttpStatusCode status)
    {
        _server.Reply(status, "");

        await _handler.CancelAsync(Stage(data: """{"id":"ext-1"}"""), default);

        Assert.Equal(HttpMethod.Delete, _server.Requests[^1].Method);
        Assert.Equal("https://subscriptions.test/api/subscriptions/ext-1", _server.Requests[^1].Uri);
    }

    [Theory]
    [InlineData("")]
    [InlineData("/absolute")]
    public void Rejects_invalid_path(string path)
    {
        Assert.NotEmpty(_handler.ValidateSettings(new HttpStageSettings(path)));
    }

    [Fact]
    public void Accepts_relative_path()
    {
        Assert.Empty(_handler.ValidateSettings(new HttpStageSettings("v1/subscriptions")));
    }

    private static StageContext Stage(string? attributes = null, string? data = null) =>
        new(1, 2, "auto-sub", "req-1",
            new DocumentContext("doc-1", "contract", attributes, null),
            new HttpStageSettings("subscriptions"),
            data);

    private sealed record RecordedRequest(HttpMethod Method, string Uri, string? IdempotencyKey, string? Body);

    private sealed class FakeServer : HttpMessageHandler
    {
        private (HttpStatusCode Status, string Body) _reply = (HttpStatusCode.OK, "{}");

        public List<RecordedRequest> Requests { get; } = [];

        public void Reply(HttpStatusCode status, string body) => _reply = (status, body);

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(new RecordedRequest(
                request.Method,
                request.RequestUri!.AbsoluteUri,
                request.Headers.TryGetValues("Idempotency-Key", out var keys) ? keys.Single() : null,
                request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken)));
            return new HttpResponseMessage(_reply.Status)
            {
                Content = new StringContent(_reply.Body, Encoding.UTF8, "application/json"),
            };
        }
    }
}
