namespace Invest.Sign.Router.PosDb;

/// <summary>Настройки Postgres. Учётные данные берутся из конфигурации/секретов, никогда не из репозитория.</summary>
public sealed class PostgresOptions
{
    public const string SectionName = "Postgres";

    /// <summary>Строка подключения Npgsql к базе состояния сервиса.</summary>
    public string ConnectionString { get; set; } = "";

    /// <summary>Тайм-аут одной SQL-команды.</summary>
    public int CommandTimeoutSeconds { get; set; } = 30;

    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(ConnectionString))
        {
            errors.Add($"{nameof(ConnectionString)} is required.");
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
            throw new ArgumentException($"Invalid Postgres options: {string.Join(" ", errors)}");
        }
    }
}
