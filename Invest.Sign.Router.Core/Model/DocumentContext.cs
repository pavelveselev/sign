using System.Globalization;
using System.Text.Json;

namespace Invest.Sign.Router.Core.Model;

/// <summary>
/// Document data available to route conditions: identity, type, attributes and content hash.
/// Attributes are a JSON object (open question 3). Values are read on demand.
/// </summary>
/// <remarks>
/// <c>GetXxx</c> methods are strict: a missing, null or malformed attribute throws
/// <see cref="DocumentAttributeException"/>, so a condition never silently evaluates on absent data.
/// <c>GetXxxOrNull</c> methods return null for a missing or null attribute and still throw on a malformed value.
/// Numbers are accepted as JSON numbers or as strings in invariant culture.
/// </remarks>
public sealed class DocumentContext
{
    private readonly JsonElement _attributes;

    public DocumentContext(string externalDocId, string docType, string? attributesJson, string? contentHash)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(externalDocId);
        ArgumentException.ThrowIfNullOrWhiteSpace(docType);

        ExternalDocId = externalDocId;
        DocType = docType;
        ContentHash = string.IsNullOrWhiteSpace(contentHash) ? null : contentHash;
        _attributes = ParseAttributes(attributesJson);
    }

    public string ExternalDocId { get; }

    public string DocType { get; }

    /// <summary>Hash of the source document, may be absent (open question 4).</summary>
    public string? ContentHash { get; }

    public bool Has(string name) =>
        _attributes.TryGetProperty(name, out var value) && value.ValueKind != JsonValueKind.Null;

    public string GetString(string name) => GetStringOrNull(name) ?? throw Missing(name);

    public string? GetStringOrNull(string name) =>
        TryGet(name, out var value)
            ? value.ValueKind switch
            {
                JsonValueKind.String => value.GetString(),
                JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False => value.GetRawText(),
                _ => throw Malformed(name, "string"),
            }
            : null;

    public decimal GetDecimal(string name) => GetDecimalOrNull(name) ?? throw Missing(name);

    public decimal? GetDecimalOrNull(string name)
    {
        if (!TryGet(name, out var value))
        {
            return null;
        }

        if (value.ValueKind == JsonValueKind.Number && value.TryGetDecimal(out var number))
        {
            return number;
        }

        if (value.ValueKind == JsonValueKind.String
            && decimal.TryParse(value.GetString(), NumberStyles.Number, CultureInfo.InvariantCulture, out number))
        {
            return number;
        }

        throw Malformed(name, "decimal");
    }

    public long GetInt64(string name) => GetInt64OrNull(name) ?? throw Missing(name);

    public long? GetInt64OrNull(string name)
    {
        if (!TryGet(name, out var value))
        {
            return null;
        }

        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number))
        {
            return number;
        }

        if (value.ValueKind == JsonValueKind.String
            && long.TryParse(value.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out number))
        {
            return number;
        }

        throw Malformed(name, "integer");
    }

    public bool GetBoolean(string name) => GetBooleanOrNull(name) ?? throw Missing(name);

    public bool? GetBooleanOrNull(string name)
    {
        if (!TryGet(name, out var value))
        {
            return null;
        }

        return value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.String when bool.TryParse(value.GetString(), out var parsed) => parsed,
            _ => throw Malformed(name, "boolean"),
        };
    }

    private bool TryGet(string name, out JsonElement value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        return _attributes.TryGetProperty(name, out value) && value.ValueKind != JsonValueKind.Null;
    }

    private static JsonElement ParseAttributes(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return EmptyObject();
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json);
        }
        catch (JsonException ex)
        {
            throw new DocumentAttributeException("Document attributes are not valid JSON.", ex);
        }

        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                throw new DocumentAttributeException(
                    $"Document attributes must be a JSON object, got {document.RootElement.ValueKind}.");
            }

            // Clone detaches the element from the pooled document so it can be disposed.
            return document.RootElement.Clone();
        }
    }

    private static JsonElement EmptyObject()
    {
        using var document = JsonDocument.Parse("{}");
        return document.RootElement.Clone();
    }

    private DocumentAttributeException Missing(string name) =>
        new($"Document '{ExternalDocId}' has no attribute '{name}'.");

    private DocumentAttributeException Malformed(string name, string expected) =>
        new($"Document '{ExternalDocId}' attribute '{name}' is not a valid {expected}.");
}

/// <summary>Document attributes are missing, malformed or of an unexpected type.</summary>
public sealed class DocumentAttributeException : Exception
{
    public DocumentAttributeException(string message)
        : base(message)
    {
    }

    public DocumentAttributeException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
