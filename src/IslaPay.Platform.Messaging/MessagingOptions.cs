namespace IslaPay.Platform.Messaging;

/// <summary>How to reach the broker, and how it should behave.</summary>
public sealed class MessagingOptions
{
    public string HostName { get; init; } = "localhost";
    public int Port { get; init; } = 5672;
    public string VirtualHost { get; init; } = "/";
    public string UserName { get; init; } = "guest";
    public string Password { get; init; } = "guest";

    /// <summary>
    /// Shown in the broker's connection list. Worth setting per service: when
    /// a connection misbehaves at 3am, "islapay-wallet" is the difference
    /// between knowing and guessing.
    /// </summary>
    public string ClientName { get; init; } = "islapay";

    /// <summary>
    /// The client re-establishes a dropped connection by itself, and replays
    /// its topology onto the new one. Without this, a broker restart silently
    /// stops the service until someone notices.
    /// </summary>
    public bool AutomaticRecovery { get; init; } = true;

    public TimeSpan RecoveryInterval { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// How long to wait for the broker to confirm a publish before treating it
    /// as failed. Not optional: without confirms, `basic.publish` is
    /// fire-and-forget and a "sent" message may never have existed.
    /// </summary>
    public TimeSpan ConfirmTimeout { get; init; } = TimeSpan.FromSeconds(10);
}

/// <summary>
/// Naming, straight from §7.2 of the architecture.
/// </summary>
/// <remarks>
/// Centralised because a convention that lives in prose gets misspelled, and a
/// misspelled routing key binds to nothing and fails silently — the message is
/// accepted, routed nowhere, and nobody finds out until the consumer is missed.
/// </remarks>
public static class Naming
{
    /// <summary>`x.&lt;context&gt;` — topic, durable.</summary>
    public static string Exchange(string context) => $"x.{Require(context)}";

    /// <summary>`q.&lt;consumer&gt;.&lt;context&gt;`.</summary>
    public static string Queue(string consumer, string context) =>
        $"q.{Require(consumer)}.{Require(context)}";

    /// <summary>`q.&lt;consumer&gt;.&lt;context&gt;.dlq`, alerted on depth &gt; 0.</summary>
    public static string DeadLetterQueue(string consumer, string context) =>
        $"{Queue(consumer, context)}.dlq";

    /// <summary>`&lt;aggregate&gt;.&lt;event&gt;.v&lt;n&gt;`, e.g. `transfer.completed.v1`.</summary>
    public static string RoutingKey(string aggregate, string @event, int version = 1) =>
        $"{Require(aggregate)}.{Require(@event)}.v{version}";

    private static string Require(string value) =>
        string.IsNullOrWhiteSpace(value)
            ? throw new ArgumentException("A name segment cannot be blank.", nameof(value))
            : value.Trim();
}
