using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace TransactionImport.Infrastructure.Persistence.Migrations;

/// <summary>
/// Applies embedded, versioned SQL scripts (<c>V0001__name.sql</c>) exactly once, in order, each in its own
/// transaction. A session-level advisory lock serializes concurrent runners, and stored checksums detect
/// edits to already-applied scripts.
/// </summary>
public sealed partial class MigrationRunner(NpgsqlDataSource dataSource, ILogger<MigrationRunner> logger)
{
    private const long AdvisoryLockKey = 7_402_310_411_771_520_001;

    public async Task<int> RunAsync(CancellationToken cancellationToken)
    {
        var scripts = LoadScripts();
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

        await ExecuteAsync(connection, "SELECT pg_advisory_lock(@key)", cancellationToken, ("key", AdvisoryLockKey)).ConfigureAwait(false);
        try
        {
            await ExecuteAsync(
                connection,
                """
                CREATE TABLE IF NOT EXISTS schema_migrations (
                    version     integer     PRIMARY KEY,
                    name        text        NOT NULL,
                    checksum    text        NOT NULL,
                    applied_at  timestamptz NOT NULL DEFAULT now()
                )
                """,
                cancellationToken).ConfigureAwait(false);

            var applied = await LoadAppliedAsync(connection, cancellationToken).ConfigureAwait(false);
            var appliedCount = 0;

            foreach (var script in scripts)
            {
                if (applied.TryGetValue(script.Version, out var checksum))
                {
                    if (!string.Equals(checksum, script.Checksum, StringComparison.Ordinal))
                    {
                        throw new InvalidOperationException(
                            $"Migration V{script.Version:D4} ({script.Name}) was modified after it was applied.");
                    }

                    continue;
                }

                await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
                await ExecuteAsync(connection, script.Sql, cancellationToken).ConfigureAwait(false);
                await ExecuteAsync(
                    connection,
                    "INSERT INTO schema_migrations (version, name, checksum) VALUES (@version, @name, @checksum)",
                    cancellationToken,
                    ("version", script.Version),
                    ("name", script.Name),
                    ("checksum", script.Checksum)).ConfigureAwait(false);
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

                LogApplied(logger, script.Version, script.Name);
                appliedCount++;
            }

            LogCompleted(logger, appliedCount, scripts.Count);
            return appliedCount;
        }
        finally
        {
            await ExecuteAsync(connection, "SELECT pg_advisory_unlock(@key)", CancellationToken.None, ("key", AdvisoryLockKey)).ConfigureAwait(false);
        }
    }

    private static async Task<Dictionary<int, string>> LoadAppliedAsync(NpgsqlConnection connection, CancellationToken cancellationToken)
    {
        var applied = new Dictionary<int, string>();
        await using var command = new NpgsqlCommand("SELECT version, checksum FROM schema_migrations", connection);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            applied[reader.GetInt32(0)] = reader.GetString(1);
        }

        return applied;
    }

    private static async Task ExecuteAsync(
        NpgsqlConnection connection,
        string sql,
        CancellationToken cancellationToken,
        params (string Name, object Value)[] parameters)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static List<MigrationScript> LoadScripts()
    {
        var assembly = typeof(MigrationRunner).Assembly;
        var scripts = new List<MigrationScript>();
        foreach (var resource in assembly.GetManifestResourceNames())
        {
            var match = ScriptName().Match(resource);
            if (!match.Success)
            {
                continue;
            }

            var sql = ReadResource(assembly, resource);
            var checksum = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(sql.ReplaceLineEndings("\n"))));
            scripts.Add(new MigrationScript(
                int.Parse(match.Groups["version"].Value, System.Globalization.CultureInfo.InvariantCulture),
                match.Groups["name"].Value,
                sql,
                checksum));
        }

        if (scripts.GroupBy(script => script.Version).Any(group => group.Count() > 1))
        {
            throw new InvalidOperationException("Duplicate migration version numbers found.");
        }

        return [.. scripts.OrderBy(script => script.Version)];
    }

    private static string ReadResource(Assembly assembly, string resource)
    {
        using var stream = assembly.GetManifestResourceStream(resource)
            ?? throw new InvalidOperationException($"Missing embedded migration {resource}.");
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }

    [GeneratedRegex(@"\.V(?<version>\d{4})__(?<name>[a-z0-9_]+)\.sql$")]
    private static partial Regex ScriptName();

    [LoggerMessage(Level = LogLevel.Information, Message = "Applied migration V{Version} {Name}")]
    private static partial void LogApplied(ILogger logger, int version, string name);

    [LoggerMessage(Level = LogLevel.Information, Message = "Migrations complete: {Applied} applied, {Total} known")]
    private static partial void LogCompleted(ILogger logger, int applied, int total);

    private sealed record MigrationScript(int Version, string Name, string Sql, string Checksum);
}
