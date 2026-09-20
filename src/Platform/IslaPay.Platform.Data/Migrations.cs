using System.Globalization;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using Npgsql;

namespace IslaPay.Platform.Data;

/// <summary>One module's schema and the scripts that build it.</summary>
/// <param name="Module">Also the Postgres schema name — one schema per context.</param>
/// <param name="Assembly">The module assembly the <c>.sql</c> files are embedded in.</param>
/// <param name="ResourcePrefix">
/// Namespace prefix of the embedded scripts, e.g.
/// <c>IslaPay.Ledger.Migrations.</c>. Files are applied in name order, so they
/// are numbered.
/// </param>
public sealed record MigrationSet(string Module, Assembly Assembly, string ResourcePrefix);

/// <summary>
/// Applies each module's SQL scripts, once, in order.
/// </summary>
/// <remarks>
/// <para>
/// Plain SQL files rather than a migration framework. The schema of a ledger
/// is something a reviewer, an auditor and a DBA all need to read, and a
/// generated <c>CREATE TABLE</c> buried in C# is readable by none of them.
/// There is also nothing here to learn: a file is applied if its name is not
/// in the table.
/// </para>
/// <para>
/// A checksum is recorded with each. Editing a script that has already run is
/// the mistake this catches — it leaves every existing environment on the old
/// definition and every new one on the new, and nothing complains until the
/// two disagree about a column months later.
/// </para>
/// </remarks>
public static class Migrator
{
    private const string HistoryTable = "platform.schema_migrations";

    /// <summary>
    /// An arbitrary but fixed key, so two instances starting at the same time
    /// do not both try to create the same table.
    /// </summary>
    private const long AdvisoryLockKey = 0x1514_9A7;

    public static async Task ApplyAsync(
        IDatabase database,
        IReadOnlyCollection<MigrationSet> sets,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(sets);

        await using var connection = await database.OpenAsync(cancellationToken).ConfigureAwait(false);

        await ExecuteAsync(connection, null,
            $"SELECT pg_advisory_lock({AdvisoryLockKey.ToString(CultureInfo.InvariantCulture)});",
            cancellationToken).ConfigureAwait(false);

        try
        {
            await ExecuteAsync(connection, null, """
                CREATE SCHEMA IF NOT EXISTS platform;
                CREATE TABLE IF NOT EXISTS platform.schema_migrations (
                    module      text        NOT NULL,
                    name        text        NOT NULL,
                    checksum    text        NOT NULL,
                    applied_at  timestamptz NOT NULL DEFAULT now(),
                    PRIMARY KEY (module, name)
                );
                """, cancellationToken).ConfigureAwait(false);

            foreach (var set in sets)
                await ApplySetAsync(connection, set, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            await ExecuteAsync(connection, null,
                $"SELECT pg_advisory_unlock({AdvisoryLockKey.ToString(CultureInfo.InvariantCulture)});",
                cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task ApplySetAsync(
        NpgsqlConnection connection, MigrationSet set, CancellationToken cancellationToken)
    {
        var applied = await AppliedAsync(connection, set.Module, cancellationToken)
            .ConfigureAwait(false);

        var scripts = set.Assembly.GetManifestResourceNames()
            .Where(n => n.StartsWith(set.ResourcePrefix, StringComparison.Ordinal)
                        && n.EndsWith(".sql", StringComparison.Ordinal))
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();

        if (scripts.Count == 0)
        {
            throw new InvalidOperationException(
                $"Module '{set.Module}' declares migrations but no .sql resources were found "
                + $"under '{set.ResourcePrefix}' in {set.Assembly.GetName().Name}. "
                + "The usual cause is a missing <EmbeddedResource Include=\"Migrations\\**\\*.sql\" />.");
        }

        foreach (var resource in scripts)
        {
            var name = resource[set.ResourcePrefix.Length..];
            var sql = await ReadAsync(set.Assembly, resource, cancellationToken).ConfigureAwait(false);
            var checksum = Checksum(sql);

            if (applied.TryGetValue(name, out var previous))
            {
                if (previous != checksum)
                {
                    throw new InvalidOperationException(
                        $"Migration '{set.Module}/{name}' has been edited since it was applied. "
                        + "Applied scripts are immutable; add a new one instead, or this "
                        + "environment and every other one will disagree about the schema.");
                }

                continue;
            }

            // Each script in its own transaction, so a failure half way through
            // a set leaves the earlier ones applied and recorded rather than
            // rolling the whole history back.
            await using var transaction = await connection
                .BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

            await ExecuteAsync(connection, transaction, sql, cancellationToken).ConfigureAwait(false);

            await using (var record = new NpgsqlCommand(
                $"INSERT INTO {HistoryTable} (module, name, checksum) VALUES (@m, @n, @c);",
                connection, transaction))
            {
                record.Parameters.AddWithValue("m", set.Module);
                record.Parameters.AddWithValue("n", name);
                record.Parameters.AddWithValue("c", checksum);
                await record.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task<Dictionary<string, string>> AppliedAsync(
        NpgsqlConnection connection, string module, CancellationToken cancellationToken)
    {
        var applied = new Dictionary<string, string>(StringComparer.Ordinal);

        await using var command = new NpgsqlCommand(
            $"SELECT name, checksum FROM {HistoryTable} WHERE module = @m;", connection);
        command.Parameters.AddWithValue("m", module);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            applied[reader.GetString(0)] = reader.GetString(1);

        return applied;
    }

    private static async Task<string> ReadAsync(
        Assembly assembly, string resource, CancellationToken cancellationToken)
    {
        await using var stream = assembly.GetManifestResourceStream(resource)
            ?? throw new InvalidOperationException($"Resource '{resource}' disappeared.");
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task ExecuteAsync(
        NpgsqlConnection connection, NpgsqlTransaction? transaction,
        string sql, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Line endings normalised first, so a checkout on Windows does not make
    /// every applied migration look edited.
    /// </summary>
    private static string Checksum(string sql) =>
        Convert.ToHexString(SHA256.HashData(
            Encoding.UTF8.GetBytes(sql.Replace("\r\n", "\n", StringComparison.Ordinal))));
}
