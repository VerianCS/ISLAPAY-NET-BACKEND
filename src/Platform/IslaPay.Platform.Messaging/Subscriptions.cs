using System.Text;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace IslaPay.Platform.Messaging;

/// <summary>One delivered message, before anyone has decided what it means.</summary>
public sealed record MessageEnvelope(
    string RoutingKey,
    ReadOnlyMemory<byte> Body,
    string? CorrelationId,
    string? MessageId);

/// <summary>Handles messages from one binding.</summary>
/// <remarks>
/// Must be idempotent. The outbox delivers at least once by construction, and
/// a redelivery after a broker restart or a failed ack is normal rather than
/// exceptional.
/// </remarks>
public interface IMessageHandler
{
    Task HandleAsync(MessageEnvelope message, CancellationToken cancellationToken);
}

/// <summary>
/// A module's declared interest in another context's events.
/// </summary>
/// <param name="Consumer">The consuming module — becomes <c>q.&lt;consumer&gt;.&lt;context&gt;</c>.</param>
/// <param name="Context">The producing context — its exchange is <c>x.&lt;context&gt;</c>.</param>
/// <param name="BindingPattern">A topic pattern, e.g. <c>user.*.v1</c>.</param>
/// <param name="HandlerType">Resolved per message from a fresh scope.</param>
public sealed record Subscription(
    string Consumer, string Context, string BindingPattern, Type HandlerType);

/// <summary>
/// Consumes every registered subscription.
/// </summary>
/// <remarks>
/// <para>
/// Its own connection, separate from the publisher's. A consumer that falls
/// behind gets blocked by the broker, and sharing a connection would mean a
/// slow handler stops the service from publishing anything at all.
/// </para>
/// <para>
/// A handler that throws nacks without requeue, which sends the message to the
/// queue's dead-letter queue rather than round the loop again. Requeueing a
/// message that fails deterministically is an infinite loop that looks like
/// load.
/// </para>
/// </remarks>
public sealed partial class RabbitMqSubscriber : BackgroundService
{
    private readonly IReadOnlyList<Subscription> _subscriptions;
    private readonly MessagingOptions _options;
    private readonly MessagingTopology _topology;
    private readonly IServiceScopeFactory _scopes;
    private readonly ILogger<RabbitMqSubscriber> _log;

    private IConnection? _connection;
    private readonly List<IChannel> _channels = [];

    public RabbitMqSubscriber(
        IEnumerable<Subscription> subscriptions,
        MessagingOptions options,
        MessagingTopology topology,
        IServiceScopeFactory scopes,
        ILogger<RabbitMqSubscriber> log)
    {
        ArgumentNullException.ThrowIfNull(subscriptions);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(topology);
        ArgumentNullException.ThrowIfNull(scopes);
        ArgumentNullException.ThrowIfNull(log);

        _subscriptions = [.. subscriptions];
        _options = options;
        _topology = topology;
        _scopes = scopes;
        _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (_subscriptions.Count == 0) return;

        // The queues exist before this runs, so a consumer never has to
        // declare what it is about to read.
        await _topology.Declared.WaitAsync(stoppingToken).ConfigureAwait(false);

        var factory = new ConnectionFactory
        {
            HostName = _options.HostName,
            Port = _options.Port,
            VirtualHost = _options.VirtualHost,
            UserName = _options.UserName,
            Password = _options.Password,
            ClientProvidedName = _options.ClientName + "-consumer",
            AutomaticRecoveryEnabled = _options.AutomaticRecovery,
            NetworkRecoveryInterval = _options.RecoveryInterval,
            TopologyRecoveryEnabled = _options.AutomaticRecovery,
        };

        // Retried rather than thrown.
        //
        // A BackgroundService that throws takes the host down with it by
        // default, so a broker that is slow to start — or briefly unreachable —
        // would stop the API from serving requests that have nothing to do with
        // messaging. Consuming is a background concern and degrades like one:
        // the API keeps answering, events queue up in the broker, and this
        // catches up when the broker returns.
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                _connection = await factory.CreateConnectionAsync(stoppingToken)
                    .ConfigureAwait(false);

                foreach (var subscription in _subscriptions)
                    await StartAsync(subscription, stoppingToken).ConfigureAwait(false);

                // The consumers run on the client's own threads; this only has
                // to stay alive until shutdown.
                await Task.Delay(Timeout.Infinite, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception e)
            {
                ConnectFailed(_log, e.Message, _options.RecoveryInterval.TotalSeconds);

                // Close what the failed attempt left behind, or every retry
                // adds another connection the broker has to hold open.
                var failed = Interlocked.Exchange(ref _connection, null);
                if (failed is not null)
                {
                    try
                    {
                        await failed.DisposeAsync().ConfigureAwait(false);
                    }
                    catch (Exception close) when (close is not OperationCanceledException)
                    {
                        ShutdownFailed(_log, close.Message);
                    }
                }

                _channels.Clear();

                try
                {
                    await Task.Delay(_options.RecoveryInterval, stoppingToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
        }
    }

    private async Task StartAsync(Subscription subscription, CancellationToken cancellationToken)
    {
        var channel = await _connection!.CreateChannelAsync(cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        _channels.Add(channel);

        // One unacked message at a time per consumer. Throughput is not the
        // constraint here and a small window keeps redelivery cheap.
        await channel.BasicQosAsync(0, 1, global: false, cancellationToken)
            .ConfigureAwait(false);

        var queue = _topology.QueueFor(subscription);
        var consumer = new AsyncEventingBasicConsumer(channel);

        consumer.ReceivedAsync += async (_, args) =>
        {
            var envelope = new MessageEnvelope(
                args.RoutingKey,
                args.Body.ToArray(),
                args.BasicProperties.CorrelationId,
                args.BasicProperties.MessageId);

            try
            {
                await using var scope = _scopes.CreateAsyncScope();
                var handler = (IMessageHandler)scope.ServiceProvider
                    .GetRequiredService(subscription.HandlerType);

                await handler.HandleAsync(envelope, cancellationToken).ConfigureAwait(false);
                await channel.BasicAckAsync(args.DeliveryTag, multiple: false, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                HandlerFailed(_log, queue, envelope.RoutingKey, e.Message);
                await channel.BasicNackAsync(
                    args.DeliveryTag, multiple: false, requeue: false, cancellationToken)
                    .ConfigureAwait(false);
            }
        };

        await channel.BasicConsumeAsync(queue, autoAck: false, consumer, cancellationToken)
            .ConfigureAwait(false);

        Subscribed(_log, queue, subscription.BindingPattern);
    }

    /// <summary>
    /// Closes the connection, and lets it close its own channels.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Only the connection, deliberately. Disposing each channel first races
    /// the client's own recovery bookkeeping: closing a channel makes it
    /// deregister itself from the connection, and if the connection has begun
    /// tearing down it releases a semaphore it has already disposed. The
    /// result is an <see cref="ObjectDisposedException"/> thrown from inside
    /// the library during an ordinary shutdown — intermittently, because it
    /// depends on which side gets there first.
    /// </para>
    /// <para>
    /// Swallowed as well. A broker that went away before we did leaves nothing
    /// to close, and a failure while shutting down must not fail the shutdown.
    /// </para>
    /// </remarks>
    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await base.StopAsync(cancellationToken).ConfigureAwait(false);

        var connection = Interlocked.Exchange(ref _connection, null);

        try
        {
            if (connection is not null) await connection.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            ShutdownFailed(_log, e.Message);
        }
        finally
        {
            _channels.Clear();
        }
    }

    /// <summary>Reads a message body as UTF-8 JSON.</summary>
    public static string TextOf(MessageEnvelope message)
    {
        ArgumentNullException.ThrowIfNull(message);
        return Encoding.UTF8.GetString(message.Body.Span);
    }

    [LoggerMessage(EventId = 1, Level = LogLevel.Information,
        Message = "Consuming {queue} bound to {pattern}")]
    private static partial void Subscribed(ILogger logger, string queue, string pattern);

    [LoggerMessage(EventId = 2, Level = LogLevel.Error,
        Message = "Handler for {queue} rejected {routingKey}; dead-lettered. {error}")]
    private static partial void HandlerFailed(
        ILogger logger, string queue, string routingKey, string error);

    [LoggerMessage(EventId = 3, Level = LogLevel.Warning,
        Message = "Could not consume: {error}. Retrying in {seconds}s.")]
    private static partial void ConnectFailed(ILogger logger, string error, double seconds);

    [LoggerMessage(EventId = 4, Level = LogLevel.Debug,
        Message = "The consumer connection did not close cleanly: {error}")]
    private static partial void ShutdownFailed(ILogger logger, string error);
}
