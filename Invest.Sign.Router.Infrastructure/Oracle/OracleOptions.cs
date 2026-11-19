using System.Text.RegularExpressions;

namespace Invest.Sign.Router.Infrastructure.Oracle;

/// <summary>Настройки Oracle. Учётные данные берутся из конфигурации/секретов, никогда не из репозитория.</summary>
public sealed partial class OracleOptions
{
    public const string SectionName = "Oracle";

    /// <summary>
    /// Строка подключения ODP.NET. Сессия должна разрешать таблицы <c>SGN_*</c> без указания схемы,
    /// т. е. подключаться от имени владельца схемы сервиса.
    /// </summary>
    public string ConnectionString { get; set; } = "";

    /// <summary>Схема объектов внешнего контракта (db/contract). Идентификатор Oracle без кавычек.</summary>
    public string ExternalSchema { get; set; } = "";

    /// <summary>Тайм-аут одной SQL-команды.</summary>
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

    // Имя схемы подставляется прямо в текст SQL, поэтому ограничено безопасным идентификатором.
    [GeneratedRegex("^[A-Za-z][A-Za-z0-9_$#]{0,127}$")]
    private static partial Regex IdentifierPattern();
}
