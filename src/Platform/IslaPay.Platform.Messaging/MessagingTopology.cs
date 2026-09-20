using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace IslaPay.Platform.Messaging;

/// <summary>
/// Declares every exchange and queue before anything publishes.
/// </summary>
/// <remarks>
/// <para>
/// A topic exchange drops what it cannot route, silently and by design. So a
/// message published before its consumer's queue exists is simply gone — no
/// error, no dead letter, nothing in a log. That is not a theoretical risk
/// here: this process both publishes and consumes, so on the first deployment
/// of a new consumer the outbox poller and the subscriber race, and the poller
/// usually wins because it has less to do.
/// </para>
/// <para>
/// Declaring the topology once, up front, removes the race rather than making
/// it less likely. <see cref="Declared"/> is what the publisher and the
/// subscriber both wait on, so neither can run ahead of it.
/// </para>
/// <para>
/// It does not block startup. If the broker is unreachable the API still
/// serves requests, the outbox accumulates, and this keeps retrying — nothing
/// is lost, because nothing has been published.
/// </para>
/// </remarks>
public sealed partial class MessagingTopology : BackgroundService
{
    private readonly TaskCompletionSource _declared =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private readonly IReadOnlyList<Subscription> _subscriptions;
    private readonly MessagingOptions _options;
    private readonly ILogger<MessagingTopology> _log;

    public MessagingTopology(
        IEnumerable<Subscription> subscriptions,
        MessagingOptions options,
        ILogger<MessagingTopology> log)
    {
        ArgumentNullException.ThrowIfNull(subscriptions);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(log);

        _subscriptions = [.. subscriptions];
        _options = options;
        _log = log;
    }

    /// <summary>Completes once every exchange and queue exists.</summary>
    public Task Declared => _declared.Task;

    /// <summary>The queue this subscription consumes, suffix applied.</summary>
    public string QueueFor(Subscription subscription)
    {
        ArgumentNullException.ThrowIfNull(subscription);
        return Naming.Queue(ConsumerName(subscription), subscription.Context);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (_subscriptions.Count == 0)
        {
            // Nothing to bind, so nothing to wait for.
            _declared.TrySetResult();
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var bus = new RabbitMqBus(_options);

                foreach (var subscription in _subscriptions)
                {
                    await bus.DeclareExchangeAsync(subscription.Context, stoppingToken)
                        .ConfigureAwait(false);
                    await bus.DeclareQueueAsync(
                        ConsumerName(subscription),
                        subscription.Context,
                        subscription.BindingPattern,
                        stoppingToken).ConfigureAwait(false);
                }

                Ready(_log, _subscriptions.Count);
                _declared.TrySetResult();
                return;
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception e)
            {
                DeclareFailed(_log, e.Message, _options.RecoveryInterval.TotalSeconds);

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

    /// <summary>
    /// The consumer name, with the configured suffix.
    /// </summary>
    /// <remarks>
    /// A queue's name is the identity of a consumer group — see
    /// <see cref="MessagingOptions.ConsumerSuffix"/>.
    /// </remarks>
    private string ConsumerName(Subscription subscription) =>
        _options.ConsumerSuffix is { Length: > 0 } suffix
            ? $"{subscription.Consumer}-{suffix}"
            : subscription.Consumer;

    [LoggerMessage(EventId = 1, Level = LogLevel.Information,
        Message = "Messaging topology declared: {count} subscription(s) bound.")]
    private static partial void Ready(ILogger logger, int count);

    [LoggerMessage(EventId = 2, Level = LogLevel.Warning,
        Message = "Could not declare the messaging topology: {error}. Retrying in {seconds}s.")]
    private static partial void DeclareFailed(ILogger logger, string error, double seconds);
}
