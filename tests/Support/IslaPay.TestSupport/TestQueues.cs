using IslaPay.Platform.Messaging;
using RabbitMQ.Client;

namespace IslaPay.TestSupport;

/// <summary>
/// Removes the queues a test run declared.
/// </summary>
/// <remarks>
/// <para>
/// Not housekeeping — a correctness problem for anyone running the suite more
/// than once. Each run uses its own consumer suffix, so each run declares a
/// fresh pair of <em>durable quorum</em> queues, and a quorum queue is a Raft
/// cluster that the broker keeps for ever. After a few dozen runs on one
/// machine the broker slows down; after a few hundred, declaring a queue times
/// out and every test that publishes fails with something that looks nothing
/// like the cause.
/// </para>
/// <para>
/// That is exactly how it was found: 311 leaked queues and a suite that had
/// started failing in four places at once. CI never noticed, because CI throws
/// the broker away after every run.
/// </para>
/// </remarks>
public static class TestQueues
{
    /// <summary>
    /// Deletes the queue and dead-letter queue for each consumer/context pair.
    /// </summary>
    public static async Task DeleteAsync(
        MessagingOptions options,
        string consumerSuffix,
        params (string Consumer, string Context)[] subscriptions)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(subscriptions);

        if (string.IsNullOrWhiteSpace(consumerSuffix)) return;

        try
        {
            var factory = new ConnectionFactory
            {
                HostName = options.HostName,
                Port = options.Port,
                UserName = options.UserName,
                Password = options.Password,
                RequestedConnectionTimeout = TimeSpan.FromSeconds(5),
            };

            await using var connection = await factory.CreateConnectionAsync();
            await using var channel = await connection.CreateChannelAsync();

            foreach (var (consumer, context) in subscriptions)
            {
                var queue = Naming.Queue($"{consumer}-{consumerSuffix}", context);
                await channel.QueueDeleteAsync(queue, ifUnused: false, ifEmpty: false);
                await channel.QueueDeleteAsync($"{queue}.dlq", ifUnused: false, ifEmpty: false);
            }
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            // A broker that is already gone leaves nothing to clean up, and a
            // cleanup failure must not turn a passing run red.
        }
    }
}
