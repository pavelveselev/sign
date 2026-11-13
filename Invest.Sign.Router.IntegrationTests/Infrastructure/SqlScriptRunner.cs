using System.Text;
using System.Text.RegularExpressions;
using Oracle.ManagedDataAccess.Client;

namespace Invest.Sign.Router.IntegrationTests.Infrastructure;

/// <summary>
/// Runs the numbered db/ scripts the way SQL*Plus would: SQL statements end with <c>;</c>,
/// PL/SQL units end with <c>/</c> on its own line, <c>&amp;&amp;name</c> substitution variables are replaced.
/// Only the subset of SQL*Plus syntax used by the repository scripts is supported.
/// </summary>
public static partial class SqlScriptRunner
{
    public static async Task RunFolderAsync(
        string connectionString, string folder, IReadOnlyDictionary<string, string>? variables = null)
    {
        await using var connection = new OracleConnection(connectionString);
        await connection.OpenAsync();

        foreach (var file in Directory.GetFiles(folder, "*.sql").Order(StringComparer.Ordinal))
        {
            var script = await File.ReadAllTextAsync(file);
            foreach (var (statement, index) in Split(Substitute(script, variables)).Select((s, i) => (s, i)))
            {
                await using var command = connection.CreateCommand();
                command.CommandText = statement;
                try
                {
                    await command.ExecuteNonQueryAsync();
                }
                catch (OracleException ex)
                {
                    throw new InvalidOperationException(
                        $"{Path.GetFileName(file)}, statement #{index + 1} failed: {ex.Message}{Environment.NewLine}{statement}", ex);
                }
            }
        }

        // CREATE PROCEDURE with compilation errors may succeed with a warning; catch it here.
        await using var check = connection.CreateCommand();
        check.CommandText = "SELECT LISTAGG(object_name, ', ') FROM user_objects WHERE status = 'INVALID'";
        if (await check.ExecuteScalarAsync() is string invalid && invalid.Length > 0)
        {
            throw new InvalidOperationException($"Invalid objects after running {folder}: {invalid}");
        }
    }

    public static IEnumerable<string> Split(string script)
    {
        var current = new StringBuilder();
        var plsql = false;

        foreach (var line in script.Replace("\r\n", "\n").Split('\n'))
        {
            var trimmed = line.Trim();
            if (current.Length == 0)
            {
                if (trimmed.Length == 0 || trimmed.StartsWith("--", StringComparison.Ordinal))
                {
                    continue;
                }

                plsql = PlSqlStart().IsMatch(trimmed);
            }

            if (plsql)
            {
                if (trimmed == "/")
                {
                    yield return current.ToString().TrimEnd();
                    current.Clear();
                }
                else
                {
                    current.Append(line).Append('\n');
                }

                continue;
            }

            current.Append(line).Append('\n');
            if (!trimmed.StartsWith("--", StringComparison.Ordinal) && trimmed.EndsWith(';'))
            {
                yield return current.ToString().TrimEnd().TrimEnd(';');
                current.Clear();
            }
        }

        if (current.ToString().Trim().Length > 0)
        {
            throw new InvalidOperationException($"Unterminated statement at the end of the script:\n{current}");
        }
    }

    private static string Substitute(string script, IReadOnlyDictionary<string, string>? variables)
    {
        if (variables is not null)
        {
            foreach (var (name, value) in variables)
            {
                script = script.Replace("&&" + name, value, StringComparison.OrdinalIgnoreCase);
            }
        }

        var unresolved = Variable().Match(script);
        return unresolved.Success
            ? throw new InvalidOperationException($"Substitution variable {unresolved.Value} has no value.")
            : script;
    }

    [GeneratedRegex(@"^(CREATE\s+(OR\s+REPLACE\s+)?(PROCEDURE|FUNCTION|PACKAGE|TRIGGER|TYPE)|BEGIN|DECLARE)\b", RegexOptions.IgnoreCase)]
    private static partial Regex PlSqlStart();

    [GeneratedRegex(@"&&?\w+")]
    private static partial Regex Variable();
}
