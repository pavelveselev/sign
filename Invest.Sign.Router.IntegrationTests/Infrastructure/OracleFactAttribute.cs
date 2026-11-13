using System.Diagnostics;

namespace Invest.Sign.Router.IntegrationTests.Infrastructure;

/// <summary>A fact that needs Oracle in Docker. Skipped, with the reason shown, when Docker is not available.</summary>
public sealed class OracleFactAttribute : FactAttribute
{
    public OracleFactAttribute()
    {
        if (DockerAvailability.SkipReason is { } reason)
        {
            Skip = reason;
        }
    }
}

/// <inheritdoc cref="OracleFactAttribute"/>
public sealed class OracleTheoryAttribute : TheoryAttribute
{
    public OracleTheoryAttribute()
    {
        if (DockerAvailability.SkipReason is { } reason)
        {
            Skip = reason;
        }
    }
}

public static class DockerAvailability
{
    private static readonly Lazy<string?> Reason = new(Check);

    /// <summary>Null when Docker is reachable, otherwise why Oracle tests are skipped.</summary>
    public static string? SkipReason => Reason.Value;

    private static string? Check()
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo("docker", "info --format {{.ServerVersion}}")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            });
            if (process is null)
            {
                return "Docker is not available: cannot start the docker CLI.";
            }

            if (!process.WaitForExit(TimeSpan.FromSeconds(20)))
            {
                process.Kill();
                return "Docker is not available: 'docker info' timed out.";
            }

            return process.ExitCode == 0
                ? null
                : $"Docker is not available: {process.StandardError.ReadToEnd().Trim()}";
        }
        catch (Exception ex)
        {
            return $"Docker is not available: {ex.Message}";
        }
    }
}
