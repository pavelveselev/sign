using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Invest.Sign.Router.Core.Abstractions;

namespace Invest.Sign.Router.Infrastructure.Stages;

/// <summary>Настройки HTTP-клиента <see cref="HttpStageHandler"/>.</summary>
public sealed class HttpStageOptions
{
    public const string SectionName = "HttpStage";

    /// <summary>Абсолютный адрес сервиса (http или https); пути этапов задаются относительно него.</summary>
    public string BaseAddress { get; set; } = "";

    /// <summary>Тайм-аут одного HTTP-запроса. Должен быть меньше <c>Engine:LeaseDuration</c>.</summary>
    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary><see cref="BaseAddress"/> с завершающим <c>/</c>, чтобы относительные пути добавлялись к нему, а не заменяли последний сегмент.</summary>
    public Uri GetBaseUri() => new(BaseAddress.EndsWith('/') ? BaseAddress : BaseAddress + "/", UriKind.Absolute);

    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();
        if (!Uri.TryCreate(BaseAddress, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
        {
            errors.Add($"{nameof(BaseAddress)} must be an absolute http or https URI, got '{BaseAddress}'.");
        }

        if (Timeout <= TimeSpan.Zero)
        {
            errors.Add($"{nameof(Timeout)} must be positive.");
        }

        return errors;
    }
}

/// <summary>Настройки HTTP-этапа.</summary>
/// <param name="Path">Путь ресурса относительно <see cref="HttpStageOptions.BaseAddress"/>, например <c>subscriptions</c>.</param>
public sealed record HttpStageSettings(string Path);

/// <summary>
/// Этап, выполняемый внешним HTTP-сервисом.
/// </summary>
/// <remarks>
/// <para>Протокол:</para>
/// <list type="bullet">
/// <item><c>POST {Path}</c> с заголовком <c>Idempotency-Key: {requestId}</c> и телом
/// <c>{ requestId, docId, docType, stageKey, attributes }</c> запускает этап.</item>
/// <item><c>GET {Path}/{id}</c> возвращает текущее состояние; <c>id</c> — из ответа на POST, иначе <c>requestId</c>.</item>
/// <item><c>DELETE {Path}/{id}</c> отменяет этап по истечении срока; 404 и 410 считаются успехом.</item>
/// <item>Ответ 2xx: <c>{ "status": "DONE" | "PENDING" | "REJECTED" | "FAILED", "id": "...", "reason": "..." }</c>.</item>
/// </list>
/// <para>
/// Ответы 408, 429, 5xx, сетевые ошибки и тайм-ауты — технические ошибки, движок повторяет шаг; благодаря
/// <c>Idempotency-Key</c> повтор POST безопасен. Остальные 4xx — бизнес-ошибка <see cref="StageOutcome.Failed"/>.
/// В данных этапа хранится <c>{"id": "..."}</c>.
/// </para>
/// </remarks>
public sealed class HttpStageHandler(HttpClient client) : IStageHandler
{
    private const int MaxBodyInReason = 500;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<StageOutcome> StartAsync(StageContext stage, CancellationToken cancellationToken)
    {
        var settings = stage.GetSettings<HttpStageSettings>();
        using var attributes = JsonDocument.Parse(stage.Document.AttributesJson);
        using var request = new HttpRequestMessage(HttpMethod.Post, settings.Path)
        {
            Content = JsonContent.Create(new
            {
                requestId = stage.RequestId,
                docId = stage.Document.ExternalDocId,
                docType = stage.Document.DocType,
                stageKey = stage.StageKey,
                attributes = attributes.RootElement,
            }, options: Json),
        };
        request.Headers.Add("Idempotency-Key", stage.RequestId);

        using var response = await client.SendAsync(request, cancellationToken);
        return await MapAsync(response, stage, cancellationToken);
    }

    public async Task<StageOutcome> CheckAsync(StageContext stage, CancellationToken cancellationToken)
    {
        using var response = await client.GetAsync(ResourceUri(stage), cancellationToken);
        return await MapAsync(response, stage, cancellationToken);
    }

    public async Task CancelAsync(StageContext stage, CancellationToken cancellationToken)
    {
        using var response = await client.DeleteAsync(ResourceUri(stage), cancellationToken);
        if (response.IsSuccessStatusCode || response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Gone)
        {
            return;
        }

        throw new HttpRequestException(
            $"Cancelling stage '{stage.StageKey}' returned {(int)response.StatusCode}.", null, response.StatusCode);
    }

    public IReadOnlyList<string> ValidateSettings(object? settings) => settings switch
    {
        HttpStageSettings { Path: var path } when string.IsNullOrWhiteSpace(path) => ["path must not be empty"],
        HttpStageSettings { Path: var path } when !Uri.TryCreate(path, UriKind.Relative, out _) || path.StartsWith('/') =>
            [$"path '{path}' must be relative to the base address and must not start with '/'"],
        HttpStageSettings => [],
        _ => [$"expected {nameof(HttpStageSettings)}, got {settings?.GetType().Name ?? "null"}"],
    };

    private static string ResourceUri(StageContext stage)
    {
        var id = stage.Data is null ? null : JsonSerializer.Deserialize<StageData>(stage.Data, Json)?.Id;
        return $"{stage.GetSettings<HttpStageSettings>().Path.TrimEnd('/')}/{Uri.EscapeDataString(id ?? stage.RequestId)}";
    }

    private static async Task<StageOutcome> MapAsync(
        HttpResponseMessage response, StageContext stage, CancellationToken cancellationToken)
    {
        var code = (int)response.StatusCode;
        if (code is 408 or 429 or >= 500)
        {
            throw new HttpRequestException(
                $"Stage '{stage.StageKey}' request returned {code}.", null, response.StatusCode);
        }

        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            return new StageOutcome.Failed(
                $"HTTP {code}: {(body.Length <= MaxBodyInReason ? body : body[..MaxBodyInReason])}");
        }

        StageResponse? result;
        try
        {
            result = await response.Content.ReadFromJsonAsync<StageResponse>(Json, cancellationToken);
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException($"Stage '{stage.StageKey}' response is not valid JSON.", ex);
        }

        var data = string.IsNullOrEmpty(result?.Id) ? null : JsonSerializer.Serialize(new StageData(result.Id), Json);
        return result?.Status?.ToUpperInvariant() switch
        {
            "DONE" => new StageOutcome.Done(Data: data),
            "PENDING" => new StageOutcome.Waiting(data),
            "REJECTED" => new StageOutcome.Rejected(result.Reason),
            "FAILED" => new StageOutcome.Failed(result.Reason),
            _ => throw new InvalidOperationException(
                $"Stage '{stage.StageKey}' response has unknown status '{result?.Status}'."),
        };
    }

    private sealed record StageResponse(
        [property: JsonPropertyName("status")] string? Status,
        [property: JsonPropertyName("id")] string? Id,
        [property: JsonPropertyName("reason")] string? Reason);

    private sealed record StageData([property: JsonPropertyName("id")] string Id);
}
