using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Invest.Sign.Router.PosDb;

/// <summary>
/// Контекст для <c>dotnet ef</c>. Строка подключения нужна только командам, которые обращаются к базе
/// (<c>database update</c>), и берётся из переменной окружения <c>SIGN_POSTGRES</c>.
/// </summary>
/// <example>
/// <code>
/// dotnet ef migrations add &lt;Name&gt; --project Invest.Sign.Router.PosDb
/// dotnet ef migrations script --idempotent --project Invest.Sign.Router.PosDb -o db/postgres/schema.sql
/// </code>
/// </example>
internal sealed class DesignTimeSignDbContextFactory : IDesignTimeDbContextFactory<SignDbContext>
{
    public SignDbContext CreateDbContext(string[] args)
    {
        var connectionString = Environment.GetEnvironmentVariable("SIGN_POSTGRES") ?? "Host=localhost;Database=sign";
        return new SignDbContext(new DbContextOptionsBuilder<SignDbContext>().UseNpgsql(connectionString).Options);
    }
}
