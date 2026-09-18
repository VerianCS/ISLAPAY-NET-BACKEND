using System.Text.Json;
using IslaPay.Contracts;
using RabbitMQ.Client;

namespace IslaPay.Platform.Messaging;

/// <summary>Publishes a domain event onto the bus.</summary>
public interface IEventPublisher
{
    /// <summary>
    /// Publishes and waits for the broker to confirm it.
    /// </summary>
    /// <param name="context">The bounded context — becomes `x.&lt;context&gt;`.</param>
    /// <param name="routingKey">From <see cref="Naming.RoutingKey"/>.</param>
    /// <param name="payload">Serialised with the contracts' own options.</param>
    /// <param name="correlationId">Ties this to the request that caused it.</param>
    /// <param name="causationId">The message that caused this one, if any.</param>
    Task PublishAsync<T>(
        string context,
        string routingKey,
        T payload,
        string? correlationId = null,
        string? causationId = null,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// A RabbitMQ connection and publisher.
/// </summary>
/// <remarks>
/// <para>
/// Every publish is persistent and confirmed. Both matter and neither is the
/// default: a non-persistent message is lost when the broker restarts, and
/// without confirms `basic.publish` returns as soon as the bytes leave the
/// socket, so a handler can report success for a message the broker never
/// accepted. For a payments platform that is the difference between a
/// notification not arriving and a ledger nobody can reconcile.
/// </para>
/// <para>
/// This is the direct publisher. It is correct to use where the publish is not
/// part of a database transaction; where it is, the outbox in §7.3 is what
/// avoids the dual write, and this is what the outbox poller calls.
/// </para>
/// </remarks>
public sealed class RabbitMqBus : IEventPublisher, IAsyncDisposable
{
    private readonly MessagingOptions _options;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private IConnection? _connection;
    private IChannel? _channel;

    public RabbitMqBus(MessagingOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options;
    }

    /// <summary>Whether the connection is currently up.</summary>
    public bool IsConnected => _connection?.IsOpen == true;

    /// <summary>
    /// Opens the connection and channel. Safe to call repeatedly; only the
    /// first caller connects.
    /// </summary>
    public async Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        if (_channel is { IsOpen: true }) return;

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_channel is { IsOpen: true }) return;

            var factory = new ConnectionFactory
            {
                HostName = _options.HostName,
                Port = _options.Port,
                VirtualHost = _options.VirtualHost,
                UserName = _options.UserName,
                Password = _options.Password,
                ClientProvidedName = _options.ClientName,
                AutomaticRecoveryEnabled = _options.AutomaticRecovery,
                NetworkRecoveryInterval = _options.RecoveryInterval,
                TopologyRecoveryEnabled = _options.AutomaticRecovery,
            };

            _connection = await factory.CreateConnectionAsync(cancellationToken)
                .ConfigureAwait(false);

            // Publisher confirms on: a publish that is not acknowledged is a
            // publish that did not happen, and the caller must learn that.
            _channel = await _connection.CreateChannelAsync(
                new CreateChannelOptions(
                    publisherConfirmationsEnabled: true,
                    publisherConfirmationTrackingEnabled: true),
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Declares a durable topic exchange for a bounded context.
    /// </summary>
    public async Task DeclareExchangeAsync(
        string context,
        CancellationToken cancellationToken = default)
    {
        var channel = await RequireChannelAsync(cancellationToken).ConfigureAwait(false);
        await channel.ExchangeDeclareAsync(
            exchange: Naming.Exchange(context),
            type: ExchangeType.Topic,
            durable: true,
            autoDelete: false,
            cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Declares a consumer's queue and binds it, with its dead-letter queue.
    /// </summary>
    /// <remarks>
    /// A quorum queue, per §7.1: it replicates across brokers, so a node
    /// failure does not take the queue's contents with it. Classic queues are
    /// only appropriate for fan-out nobody would miss.
    /// <para>
    /// The DLQ is declared alongside rather than later. A queue without one
    /// silently drops what it cannot process, and the first sign of trouble is
    /// a customer asking where their notification went.
    /// </para>
    /// </remarks>
    public async Task DeclareQueueAsync(
        string consumer,
        string context,
        string bindingPattern,
        CancellationToken cancellationToken = default)
    {
        var channel = await RequireChannelAsync(cancellationToken).ConfigureAwait(false);

        var exchange = Naming.Exchange(context);
        var queue = Naming.Queue(consumer, context);
        var dlq = Naming.DeadLetterQueue(consumer, context);

        await channel.QueueDeclareAsync(
            queue: dlq,
            durable: true,
            exclusive: false,
            autoDelete: false,
            arguments: new Dictionary<string, object?> { ["x-queue-type"] = "quorum" },
            cancellationToken: cancellationToken).ConfigureAwait(false);

        await channel.QueueDeclareAsync(
            queue: queue,
            durable: true,
            exclusive: false,
            autoDelete: false,
            arguments: new Dictionary<string, object?>
            {
                ["x-queue-type"] = "quorum",
                ["x-dead-letter-exchange"] = string.Empty,
                ["x-dead-letter-routing-key"] = dlq,
            },
            cancellationToken: cancellationToken).ConfigureAwait(false);

        await channel.QueueBindAsync(
            queue: queue,
            exchange: exchange,
            routingKey: bindingPattern,
            cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task PublishAsync<T>(
        string context,
        string routingKey,
        T payload,
        string? correlationId = null,
        string? causationId = null,
        CancellationToken cancellationToken = default)
    {
        var channel = await RequireChannelAsync(cancellationToken).ConfigureAwait(false);

        var body = JsonSerializer.SerializeToUtf8Bytes(payload, IslaPayJson.Options);
        var messageId = Guid.NewGuid().ToString("N");

        var properties = new BasicProperties
        {
            ContentType = "application/json",
            // Persistent. A transient message does not survive a broker
            // restart, and "the broker restarted" is not an acceptable reason
            // for a payment event to vanish.
            DeliveryMode = DeliveryModes.Persistent,
            MessageId = messageId,
            CorrelationId = correlationId,
            Timestamp = new AmqpTimestamp(DateTimeOffset.UtcNow.ToUnixTimeSeconds()),
            Headers = new Dictionary<string, object?>
            {
                ["causation-id"] = causationId,
                ["schema-version"] = ExtractVersion(routingKey),
            },
        };

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_options.ConfirmTimeout);

        // Waits for the broker's ack. Throws if it never comes, so a caller
        // cannot mistake "sent" for "accepted".
        await channel.BasicPublishAsync(
            exchange: Naming.Exchange(context),
            routingKey: routingKey,
            mandatory: false,
            basicProperties: properties,
            body: body,
            cancellationToken: timeout.Token).ConfigureAwait(false);
    }

    private async Task<IChannel> RequireChannelAsync(CancellationToken cancellationToken)
    {
        await ConnectAsync(cancellationToken).ConfigureAwait(false);
        return _channel ?? throw new InvalidOperationException(
            "No channel: the bus is not connected.");
    }

    private static string ExtractVersion(string routingKey)
    {
        var lastDot = routingKey.LastIndexOf('.');
        return lastDot >= 0 ? routingKey[(lastDot + 1)..] : "v1";
    }

    public async ValueTask DisposeAsync()
    {
        if (_channel is not null) await _channel.DisposeAsync().ConfigureAwait(false);
        if (_connection is not null) await _connection.DisposeAsync().ConfigureAwait(false);
        _gate.Dispose();
    }
}
