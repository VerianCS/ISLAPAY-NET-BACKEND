using System.Text;
using System.Text.Json;
using IslaPay.Platform.Messaging;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;

namespace IslaPay.Platform.Messaging.Tests;

/// <summary>
/// Runs against a real broker.
/// </summary>
/// <remarks>
/// Deliberately not a mock. What is worth testing here is precisely the part a
/// mock cannot have: that the exchange and queue declarations the broker
/// accepts are the ones we wrote, that a quorum queue can actually be created,
/// that publisher confirms come back, and that a published message is routed
/// to the queue a consumer is bound to. A mocked IChannel asserts that we
/// called our own code.
/// <para>
/// Skipped, not failed, when no broker is reachable: a developer without one
/// running should not see red for something that is not their change. CI is
/// where the broker is guaranteed.
/// </para>
/// </remarks>
[Trait("Category", "Integration")]
public class RabbitMqBusTests
{
    private static MessagingOptions Options() => new()
    {
        HostName = Environment.GetEnvironmentVariable("RABBITMQ_HOST") ?? "localhost",
        ClientName = "islapay-tests",
        ConfirmTimeout = TimeSpan.FromSeconds(10),
    };

    private static async Task<bool> BrokerIsUpAsync()
    {
        try
        {
            var factory = new ConnectionFactory
            {
                HostName = Options().HostName,
                RequestedConnectionTimeout = TimeSpan.FromSeconds(3),
            };
            await using var connection = await factory.CreateConnectionAsync();
            return connection.IsOpen;
        }
        catch
        {
            return false;
        }
    }

    [SkippableFact]
    public async Task Connects_to_the_broker()
    {
        Skip.IfNot(await BrokerIsUpAsync(), "No RabbitMQ reachable.");

        await using var bus = new RabbitMqBus(Options());
        await bus.ConnectAsync();

        Assert.True(bus.IsConnected);
    }

    [SkippableFact]
    public async Task Connecting_twice_is_harmless()
    {
        Skip.IfNot(await BrokerIsUpAsync(), "No RabbitMQ reachable.");

        await using var bus = new RabbitMqBus(Options());
        await bus.ConnectAsync();
        await bus.ConnectAsync();

        Assert.True(bus.IsConnected);
    }

    [SkippableFact]
    public async Task Declares_the_topology_from_section_7()
    {
        Skip.IfNot(await BrokerIsUpAsync(), "No RabbitMQ reachable.");

        var context = $"test{Guid.NewGuid():N}"[..12];
        await using var bus = new RabbitMqBus(Options());

        await bus.DeclareExchangeAsync(context);
        // Quorum queue plus its DLQ. If the broker rejects either, this throws.
        await bus.DeclareQueueAsync("notification", context, "transfer.*.v1");

        Assert.True(bus.IsConnected);
    }

    [SkippableFact]
    public async Task A_published_event_reaches_the_queue_bound_to_it()
    {
        Skip.IfNot(await BrokerIsUpAsync(), "No RabbitMQ reachable.");

        var context = $"test{Guid.NewGuid():N}"[..12];
        var queue = Naming.Queue("notification", context);

        await using var bus = new RabbitMqBus(Options());
        await bus.DeclareExchangeAsync(context);
        await bus.DeclareQueueAsync("notification", context, "transfer.*.v1");

        var correlation = Guid.NewGuid().ToString("N");
        await bus.PublishAsync(
            context,
            Naming.RoutingKey("transfer", "completed"),
            new { userId = "42", amount = new { amount = "100.50", currency = "USD" } },
            correlationId: correlation);

        var (body, properties) = await ConsumeOneAsync(queue);

        Assert.NotNull(body);
        var json = JsonDocument.Parse(body!);
        Assert.Equal("42", json.RootElement.GetProperty("userId").GetString());
        // Money crossed the wire as a string, as the contract requires.
        Assert.Equal("100.50",
            json.RootElement.GetProperty("amount").GetProperty("amount").GetString());

        Assert.Equal(correlation, properties!.CorrelationId);
        Assert.Equal("application/json", properties.ContentType);
        // Persistent, or a broker restart loses it.
        Assert.Equal(DeliveryModes.Persistent, properties.DeliveryMode);
        Assert.NotNull(properties.MessageId);
        Assert.Equal("v1", Header(properties, "schema-version"));
    }

    [SkippableFact]
    public async Task A_routing_key_that_matches_nothing_is_not_delivered()
    {
        Skip.IfNot(await BrokerIsUpAsync(), "No RabbitMQ reachable.");

        var context = $"test{Guid.NewGuid():N}"[..12];
        var queue = Naming.Queue("notification", context);

        await using var bus = new RabbitMqBus(Options());
        await bus.DeclareExchangeAsync(context);
        await bus.DeclareQueueAsync("notification", context, "transfer.*.v1");

        // Bound to transfer.*, so a payment event must not arrive here. This
        // is the failure mode a misspelled routing key produces: accepted by
        // the broker, routed nowhere, noticed by nobody.
        await bus.PublishAsync(
            context, Naming.RoutingKey("payment", "captured"), new { id = "x" });

        var (body, _) = await ConsumeOneAsync(queue, TimeSpan.FromSeconds(2));
        Assert.Null(body);
    }

    [SkippableFact]
    public async Task Publishing_to_an_unreachable_broker_fails_rather_than_pretending()
    {
        var bus = new RabbitMqBus(new MessagingOptions
        {
            HostName = "127.0.0.1",
            Port = 5673, // nothing listens here
            ClientName = "islapay-tests",
        });

        await Assert.ThrowsAnyAsync<Exception>(
            () => bus.PublishAsync("wallet", Naming.RoutingKey("transfer", "completed"), new { }));

        await bus.DisposeAsync();
    }

    // ------------------------------------------------------------------ helpers

    private static async Task<(byte[]? Body, IReadOnlyBasicProperties? Properties)>
        ConsumeOneAsync(string queue, TimeSpan? wait = null)
    {
        var factory = new ConnectionFactory { HostName = Options().HostName };
        await using var connection = await factory.CreateConnectionAsync();
        await using var channel = await connection.CreateChannelAsync();

        var received = new TaskCompletionSource<(byte[], IReadOnlyBasicProperties)>();
        var consumer = new AsyncEventingBasicConsumer(channel);
        consumer.ReceivedAsync += (_, args) =>
        {
            received.TrySetResult((args.Body.ToArray(), args.BasicProperties));
            return Task.CompletedTask;
        };

        await channel.BasicConsumeAsync(queue, autoAck: true, consumer);

        var completed = await Task.WhenAny(
            received.Task,
            Task.Delay(wait ?? TimeSpan.FromSeconds(10)));

        return completed == received.Task
            ? (received.Task.Result.Item1, received.Task.Result.Item2)
            : (null, null);
    }

    private static string? Header(IReadOnlyBasicProperties properties, string key) =>
        properties.Headers is not null && properties.Headers.TryGetValue(key, out var value)
            ? value switch
            {
                byte[] bytes => Encoding.UTF8.GetString(bytes),
                _ => value?.ToString(),
            }
            : null;
}

/// <summary>Naming conventions are worth pinning; a typo binds to nothing.</summary>
public class NamingTests
{
    [Fact]
    public void Follows_section_7_2()
    {
        Assert.Equal("x.wallet", Naming.Exchange("wallet"));
        Assert.Equal("q.notification.wallet", Naming.Queue("notification", "wallet"));
        Assert.Equal("q.notification.wallet.dlq", Naming.DeadLetterQueue("notification", "wallet"));
        Assert.Equal("transfer.completed.v1", Naming.RoutingKey("transfer", "completed"));
        Assert.Equal("payroll.item.paid.v2", Naming.RoutingKey("payroll", "item.paid", 2));
    }

    [Fact]
    public void Rejects_a_blank_segment()
    {
        Assert.Throws<ArgumentException>(() => Naming.Exchange(" "));
        Assert.Throws<ArgumentException>(() => Naming.Queue("notification", ""));
    }
}
