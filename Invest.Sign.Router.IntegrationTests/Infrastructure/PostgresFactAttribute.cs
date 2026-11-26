namespace Invest.Sign.Router.IntegrationTests.Infrastructure;

/// <summary>Тест, которому нужен Postgres в Docker. Пропускается с указанием причины, если Docker недоступен.</summary>
public sealed class PostgresFactAttribute : FactAttribute
{
    public PostgresFactAttribute()
    {
        if (DockerAvailability.SkipReason is { } reason)
        {
            Skip = reason;
        }
    }
}
