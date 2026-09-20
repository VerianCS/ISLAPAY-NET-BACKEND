using System.Security.Cryptography;
using System.Text;
using IslaPay.Platform.Data;
using Npgsql;

namespace IslaPay.Platform.AspNet;

/// <summary>Marks an endpoint as requiring an <c>Idempotency-Key</c>.</summary>
/// <remarks>
/// Opt-in rather than blanket. A GET does not need it and a key on one would
/// only invite the client to cache what it should not.
/// </remarks>
[AttributeUsage(AttributeTargets.Method | AttributeTargets.Class)]
public sealed class IdempotentAttribute : Attribute;

/// <summary>What an attempt to claim a key came back with.</summary>
public abstract record IdempotencyOutcome
{
    /// <summary>Nobody has used this key; go ahead.</summary>
    public sealed record Claimed : IdempotencyOutcome;

    /// <summary>The same request finished earlier. Its answer is below.</summary>
    public sealed record Replay(int Status, string? Body) : IdempotencyOutcome;

    /// <summary>The same request is still running somewhere.</summary>
    public sealed record InFlight : IdempotencyOutcome;

    /// <summary>The key was used for a different request. Always a client bug.</summary>
    public sealed record Reused : IdempotencyOutcome;
}

/// <summary>Remembers what each idempotency key answered.</summary>
public sealed class IdempotencyStore
{
    private readonly IDatabase _database;

    public IdempotencyStore(IDatabase database)
    {
        ArgumentNullException.ThrowIfNull(database);
        _database = database;
    }

    /// <summary>
    /// Claims the key, or says why it cannot be claimed.
    /// </summary>
    /// <remarks>
    /// No transaction and no lock: the primary key does the work.
    /// <c>ON CONFLICT DO NOTHING</c> is atomic, so of several callers racing
    /// with the same key exactly one inserts a row, and the rest fall through
    /// to read what that one wrote.
    /// </remarks>
    public async Task<IdempotencyOutcome> ClaimAsync(
        string scope, string key, string endpoint, string requestHash,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await _database.OpenAsync(cancellationToken).ConfigureAwait(false);

        await using (var claim = new NpgsqlCommand("""
            INSERT INTO platform.idempotency (scope, key, endpoint, request_hash, status)
            VALUES (@scope, @key, @endpoint, @hash, 'in_flight')
            ON CONFLICT (scope, key) DO NOTHING;
            """, connection))
        {
            claim.Parameters.AddWithValue("scope", scope);
            claim.Parameters.AddWithValue("key", key);
            claim.Parameters.AddWithValue("endpoint", endpoint);
            claim.Parameters.AddWithValue("hash", requestHash);

            if (await claim.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 1)
                return new IdempotencyOutcome.Claimed();
        }

        await using var read = new NpgsqlCommand("""
            SELECT request_hash, status, response_status, response_body
            FROM platform.idempotency
            WHERE scope = @scope AND key = @key;
            """, connection);
        read.Parameters.AddWithValue("scope", scope);
        read.Parameters.AddWithValue("key", key);

        await using var reader = await read.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);

        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            // Claimed and then released between the two statements. Treating
            // it as in-flight is the safe answer: the client retries.
            return new IdempotencyOutcome.InFlight();
        }

        if (!string.Equals(reader.GetString(0), requestHash, StringComparison.Ordinal))
            return new IdempotencyOutcome.Reused();

        return reader.GetString(1) == "completed"
            ? new IdempotencyOutcome.Replay(
                reader.GetInt32(2), reader.IsDBNull(3) ? null : reader.GetString(3))
            : new IdempotencyOutcome.InFlight();
    }

    /// <summary>Records what the request answered, so a retry replays it.</summary>
    public async Task CompleteAsync(
        string scope, string key, int status, string? body,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await _database.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand("""
            UPDATE platform.idempotency
            SET status = 'completed', response_status = @status,
                response_body = @body, completed_at = now()
            WHERE scope = @scope AND key = @key;
            """, connection);

        command.Parameters.AddWithValue("scope", scope);
        command.Parameters.AddWithValue("key", key);
        command.Parameters.AddWithValue("status", status);
        command.Parameters.AddWithValue("body", (object?)body ?? DBNull.Value);

        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Releases a claim so the client can try again.
    /// </summary>
    /// <remarks>
    /// Used when the request failed for a reason that says nothing about
    /// whether it would fail again — a dependency being down, a crash. Keeping
    /// the claim would turn one bad moment into a key that can never be used,
    /// and the client cannot invent a new one: it is the same user intent.
    /// </remarks>
    public async Task ReleaseAsync(
        string scope, string key, CancellationToken cancellationToken = default)
    {
        await using var connection = await _database.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand("""
            DELETE FROM platform.idempotency
            WHERE scope = @scope AND key = @key AND status = 'in_flight';
            """, connection);

        command.Parameters.AddWithValue("scope", scope);
        command.Parameters.AddWithValue("key", key);

        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>A stable fingerprint of what was asked.</summary>
    public static string Fingerprint(string method, string path, ReadOnlySpan<byte> body)
    {
        var prefix = Encoding.UTF8.GetBytes($"{method} {path}\n");
        var buffer = new byte[prefix.Length + body.Length];
        prefix.CopyTo(buffer, 0);
        body.CopyTo(buffer.AsSpan(prefix.Length));
        return Convert.ToHexString(SHA256.HashData(buffer));
    }
}
