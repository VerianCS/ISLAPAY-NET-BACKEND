using System.Text.Json;
using IslaPay.Platform.Data;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace IslaPay.Platform.Messaging;

/// <summary>How eagerly the outbox is drained.</summary>
public sealed class OutboxOptions
{
    /// <summary>
    /// How long to wait after finding nothing.
    /// </summary>
    /// <remarks>
    /// A poll rather than a notification, because a poll cannot miss a row. If
    /// the delay matters, LISTEN/NOTIFY can wake it early — but the poll has
    /// to stay as the floor, or a missed notification is a message that sits
    /// there for ever.
    /// </remarks>
    public TimeSpan IdleDelay { get; init; } = TimeSpan.FromSeconds(1);

    public int BatchSize { get; init; } = 100;

    /// <summary>
    /// After this many failures a row stops being retried and waits for a
    /// human. Retrying for ever turns one poison message into a service that
    /// publishes nothing else.
    /// </summary>
    public int MaxAttempts { get; init; } = 10;

    /// <summary>
    /// Whether this process drains the outbox on a timer.
    /// </summary>
    /// <remarks>
    /// True by default. Turning it off leaves a host that serves requests and
    /// writes to the outbox but publishes nothing itself — which is what you
    /// want when draining is a separate replica's job, and what a test wants
    /// when it needs to drain at a moment of its choosing rather than race a
    /// timer. <see cref="OutboxPublisher.DrainOnceAsync"/> still works either
    /// way.
    /// </remarks>
    public bool PublishInBackground { get; init; } = true;
}

/// <summary>
/// Drains the outbox into RabbitMQ.
/// </summary>
/// <remarks>
/// <para>
/// Rows are claimed with <c>FOR UPDATE SKIP LOCKED</c>, so several instances
/// can drain the same table without blocking each other and without two of
/// them publishing the same row.
/// </para>
/// <para>
/// The cost of that is ordering: rows are taken in id order by each poller,
/// but two pollers working at once can publish row 7 before row 6. Nothing
/// here depends on global ordering yet. When something does, the fix is to
/// claim by a partition key rather than to remove SKIP LOCKED, which would
/// only move the contention.
/// </para>
/// </remarks>
public sealed partial class OutboxPublisher : BackgroundService
{
    private readonly IDatabase _database;
    private readonly IEventPublisher _publisher;
    private readonly RabbitMqBus _bus;
    private readonly OutboxOptions _options;
    private readonly ILogger<OutboxPublisher> _log;
    private readonly HashSet<string> _declared = new(StringComparer.Ordinal);

    public OutboxPublisher(
        IDatabase database,
        IEventPublisher publisher,
        RabbitMqBus bus,
        OutboxOptions options,
        ILogger<OutboxPublisher> log)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(publisher);
        ArgumentNullException.ThrowIfNull(bus);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(log);

        _database = database;
        _publisher = publisher;
        _bus = bus;
        _options = options;
        _log = log;
    }

    /// <summary>Publishes one batch. Returns how many rows it sent.</summary>
    /// <remarks>
    /// Public so a test can drain deterministically instead of waiting for a
    /// timer — a test that sleeps is a test that is slow when it passes and
    /// flaky when it fails.
    /// </remarks>
    public async Task<int> DrainOnceAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await _database.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection
            .BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        var rows = await ClaimAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
        if (rows.Count == 0)
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            return 0;
        }

        var published = 0;
        foreach (var row in rows)
        {
            try
            {
                await DeclareOnceAsync(row.Context, cancellationToken).ConfigureAwait(false);

                await _publisher.PublishAsync(
                    row.Context,
                    row.RoutingKey,
                    JsonDocument.Parse(row.Payload).RootElement,
                    row.CorrelationId,
                    row.CausationId,
                    cancellationToken).ConfigureAwait(false);

                await MarkPublishedAsync(connection, transaction, row.Id, cancellationToken)
                    .ConfigureAwait(false);
                published++;
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                // Recorded rather than thrown: one unroutable message must not
                // stop the rest of the batch, and the row stays unpublished so
                // the next pass tries again.
                await MarkFailedAsync(connection, transaction, row.Id, e.Message, cancellationToken)
                    .ConfigureAwait(false);
                PublishFailed(_log, row.Id, row.RoutingKey, e.Message);
            }
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return published;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.PublishInBackground) return;

        while (!stoppingToken.IsCancellationRequested)
        {
            int sent;
            try
            {
                sent = await DrainOnceAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                // The database or the broker is down. Neither is this loop's
                // problem to solve, and dying would mean nothing is published
                // when they come back.
                DrainFailed(_log, e.Message);
                sent = 0;
            }

            if (sent == 0)
            {
                try
                {
                    await Task.Delay(_options.IdleDelay, stoppingToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
        }
    }

    // ------------------------------------------------------------------ plumbing

    private sealed record Row(
        long Id, string Context, string RoutingKey, string Payload,
        string? CorrelationId, string? CausationId);

    private async Task<List<Row>> ClaimAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("""
            SELECT id, context, routing_key, payload::text, correlation_id, causation_id
            FROM messaging.outbox
            WHERE published_at IS NULL AND attempts < @max_attempts
            ORDER BY id
            LIMIT @batch
            FOR UPDATE SKIP LOCKED;
            """, connection, transaction);

        command.Parameters.AddWithValue("batch", _options.BatchSize);
        command.Parameters.AddWithValue("max_attempts", _options.MaxAttempts);

        var rows = new List<Row>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            rows.Add(new Row(
                reader.GetInt64(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetString(4),
                reader.IsDBNull(5) ? null : reader.GetString(5)));
        }

        return rows;
    }

    private async Task DeclareOnceAsync(string context, CancellationToken cancellationToken)
    {
        if (!_declared.Add(context)) return;
        await _bus.DeclareExchangeAsync(context, cancellationToken).ConfigureAwait(false);
    }

    private static async Task MarkPublishedAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction,
        long id, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            "UPDATE messaging.outbox SET published_at = now() WHERE id = @id;",
            connection, transaction);
        command.Parameters.AddWithValue("id", id);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task MarkFailedAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction,
        long id, string error, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("""
            UPDATE messaging.outbox
            SET attempts = attempts + 1, last_error = @error
            WHERE id = @id;
            """, connection, transaction);
        command.Parameters.AddWithValue("id", id);
        command.Parameters.AddWithValue("error", error.Length > 500 ? error[..500] : error);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    [LoggerMessage(
        EventId = 1, Level = LogLevel.Warning,
        Message = "Outbox row {id} ({routingKey}) failed to publish: {error}")]
    private static partial void PublishFailed(
        ILogger logger, long id, string routingKey, string error);

    [LoggerMessage(
        EventId = 2, Level = LogLevel.Error,
        Message = "The outbox could not be drained: {error}")]
    private static partial void DrainFailed(ILogger logger, string error);
}
