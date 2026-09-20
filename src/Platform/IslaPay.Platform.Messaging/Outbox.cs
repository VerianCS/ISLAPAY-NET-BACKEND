using System.Text.Json;
using IslaPay.Platform.Data;
using IslaPay.Platform.Serialization;
using Npgsql;

namespace IslaPay.Platform.Messaging;

/// <summary>
/// Records an event to be published, in the caller's own transaction.
/// </summary>
/// <remarks>
/// <para>
/// This exists to avoid a dual write. Writing to the database and publishing
/// to the broker are two systems, and a handler that does both has two ways to
/// half-succeed: the row is committed and the message never sent, or the
/// message is sent and the transaction rolls back. The first loses an event;
/// the second announces something that did not happen, which is worse.
/// </para>
/// <para>
/// So the event is written as part of the same transaction as the data that
/// caused it, and a poller publishes it afterwards. That makes delivery
/// <em>at least once</em>: a publish that succeeds and then fails to be marked
/// will be published again. Consumers must be idempotent, and the ones here
/// are.
/// </para>
/// </remarks>
public interface IOutbox
{
    /// <summary>
    /// Enqueues within an existing transaction.
    /// </summary>
    /// <remarks>
    /// Takes the caller's connection and transaction rather than opening its
    /// own, which is the entire point: a separate transaction would restore
    /// the dual write this class exists to remove.
    /// </remarks>
    Task EnqueueAsync<T>(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string context,
        string routingKey,
        T payload,
        string? correlationId = null,
        string? causationId = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Enqueues in a transaction of its own.
    /// </summary>
    /// <remarks>
    /// For producers whose own state lives somewhere this database cannot
    /// transact with — an external identity provider, for instance. It is
    /// weaker: the external write can succeed and this can fail, so the
    /// consumer needs a way to reconcile. Do not reach for it when the causing
    /// write is in this database.
    /// </remarks>
    Task EnqueueAsync<T>(
        string context,
        string routingKey,
        T payload,
        string? correlationId = null,
        string? causationId = null,
        CancellationToken cancellationToken = default);
}

/// <inheritdoc />
public sealed class Outbox : IOutbox
{
    private const string Insert = """
        INSERT INTO messaging.outbox
            (context, routing_key, payload, correlation_id, causation_id)
        VALUES (@context, @routing_key, @payload::jsonb, @correlation, @causation);
        """;

    private readonly IDatabase _database;

    public Outbox(IDatabase database)
    {
        ArgumentNullException.ThrowIfNull(database);
        _database = database;
    }

    public async Task EnqueueAsync<T>(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string context,
        string routingKey,
        T payload,
        string? correlationId = null,
        string? causationId = null,
        CancellationToken cancellationToken = default)
    {
        await using var command = new NpgsqlCommand(Insert, connection, transaction);
        Bind(command, context, routingKey, payload, correlationId, causationId);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public Task EnqueueAsync<T>(
        string context,
        string routingKey,
        T payload,
        string? correlationId = null,
        string? causationId = null,
        CancellationToken cancellationToken = default) =>
        _database.InTransactionAsync(async (connection, transaction, ct) =>
        {
            await EnqueueAsync(
                connection, transaction, context, routingKey, payload,
                correlationId, causationId, ct).ConfigureAwait(false);
            return 0;
        }, cancellationToken: cancellationToken);

    private static void Bind<T>(
        NpgsqlCommand command, string context, string routingKey, T payload,
        string? correlationId, string? causationId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(context);
        ArgumentException.ThrowIfNullOrWhiteSpace(routingKey);

        command.Parameters.AddWithValue("context", context);
        command.Parameters.AddWithValue("routing_key", routingKey);
        command.Parameters.AddWithValue(
            "payload", JsonSerializer.Serialize(payload, IslaPayJson.Options));
        command.Parameters.AddWithValue("correlation", (object?)correlationId ?? DBNull.Value);
        command.Parameters.AddWithValue("causation", (object?)causationId ?? DBNull.Value);
    }
}
