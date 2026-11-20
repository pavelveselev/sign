using System.Security.Cryptography;

namespace Invest.Sign.Router;

/// <summary>
/// Идентификатор реплики для <c>locked_by</c> и журнала аудита: имя пода и случайный суффикс.
/// Суффикс отличает перезапущенный под с тем же именем от его предыдущего экземпляра, чья аренда ещё не истекла.
/// </summary>
public sealed record ReplicaIdentity(string Id)
{
    /// <summary>Ограничение длины основы, чтобы идентификатор с суффиксом гарантированно помещался в колонку.</summary>
    public const int MaxBaseLength = 90;

    public static ReplicaIdentity Create(RouterOptions options)
    {
        var name = options.ReplicaName;
        if (string.IsNullOrWhiteSpace(name))
        {
            name = Environment.GetEnvironmentVariable("POD_NAME");
        }

        if (string.IsNullOrWhiteSpace(name))
        {
            name = Environment.MachineName;
        }

        name = name.Trim();
        if (name.Length > MaxBaseLength)
        {
            name = name[..MaxBaseLength];
        }

        var suffix = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(4));
        return new ReplicaIdentity($"{name}-{suffix}");
    }

    public override string ToString() => Id;
}
