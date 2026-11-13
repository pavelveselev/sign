using System.Text.RegularExpressions;

namespace Invest.Sign.Router.Infrastructure.Oracle;

/// <summary>Oracle settings. Credentials come from configuration/secrets, never from the repository.</summary>
public sealed partial class OracleOptions
{
    public const string SectionName = "Oracle";

    /// <summary>
    /// ODP.NET connection string. The session must resolve the <c>SGN_*</c> tables unqualified,
    /// i.e. connect as the service schema owner.
    /// </summary>
    public string ConnectionString { get; set; } = "";

    /// <summary>Schema of the external contract objects (db/contract). Unquoted Oracle identifier.</summary>
    public string ExternalSchema { get; set; } = "";

    /// <summary>Timeout of a single SQL command.</summary>
    public int CommandTimeoutSeconds { get; set; } = 30;

    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(ConnectionString))
        {
            errors.Add($"{nameof(ConnectionString)} is required.");
        }

        if (!IdentifierPattern().IsMatch(ExternalSchema))
        {
            errors.Add($"{nameof(ExternalSchema)} must be an unquoted Oracle identifier, got '{ExternalSchema}'.");
        }

        if (CommandTimeoutSeconds < 1)
        {
            errors.Add($"{nameof(CommandTimeoutSeconds)} must be at least 1.");
        }

        return errors;
    }

    internal void EnsureValid()
    {
        var errors = Validate();
        if (errors.Count > 0)
        {
            throw new ArgumentException($"Invalid Oracle options: {string.Join(" ", errors)}");
        }
    }

    // The schema name is inlined into SQL text, so it is restricted to a safe identifier.
    [GeneratedRegex("^[A-Za-z][A-Za-z0-9_$#]{0,127}$")]
    private static partial Regex IdentifierPattern();
}
