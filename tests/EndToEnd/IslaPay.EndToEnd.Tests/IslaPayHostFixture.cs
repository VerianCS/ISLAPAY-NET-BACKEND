using System.Collections.Concurrent;
using IslaPay.Identity;
using IslaPay.TestSupport;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Hosting;

namespace IslaPay.EndToEnd.Tests;

/// <summary>
/// The whole system: every module, every dependency, nothing stubbed.
/// </summary>
/// <remarks>
/// These tests live outside any module on purpose. What they check is the
/// journey <em>between</em> contexts — Identity publishing something Wallet
/// consumes — and a test of that belongs to neither of them. Putting it inside
/// one module would also break the rule that a module's tests stay inside it.
/// </remarks>
public sealed class IslaPayHostFixture : IAsyncLifetime
{
    public KeycloakFixture Keycloak { get; } = new();

    public PostgresFixture Postgres { get; } = new();

    public bool Available => Keycloak.Available && Postgres.Available;

    /// <summary>Makes this run's queues its own. See MessagingOptions.ConsumerSuffix.</summary>
    public string ConsumerSuffix { get; } = $"e2e{Guid.NewGuid():N}"[..12];

    /// <summary>A vhost-free RabbitMQ on localhost, or wherever CI put one.</summary>
    public static string RabbitHost =>
        Environment.GetEnvironmentVariable("RABBITMQ_HOST") ?? "localhost";

    public async Task InitializeAsync() =>
        await Task.WhenAll(Keycloak.InitializeAsync(), Postgres.InitializeAsync());

    public async Task DisposeAsync()
    {
        // Before anything else: these are durable quorum queues, and a broker
        // that keeps one per run eventually cannot declare another.
        await TestQueues.DeleteAsync(
            new Platform.Messaging.MessagingOptions { HostName = RabbitHost },
            ConsumerSuffix,
            ("wallet", "identity"));

        await Keycloak.DisposeAsync();
        Keycloak.Dispose();
        await Postgres.DisposeAsync();
    }

    /// <summary>
    /// Builds the host.
    /// </summary>
    /// <param name="brokerPort">
    /// Overridden to an unused port by the test that proves a new user still
    /// gets a wallet when the bus is down.
    /// </param>
    public IslaPayHost Build(int? brokerPort = null) => new(this, brokerPort);
}

/// <summary>The real pipeline with the real modules.</summary>
public sealed class IslaPayHost : WebApplicationFactory<Program>
{
    private readonly IslaPayHostFixture _fixture;
    private readonly int? _brokerPort;

    public IslaPayHost(IslaPayHostFixture fixture, int? brokerPort)
    {
        _fixture = fixture;
        _brokerPort = brokerPort;
    }

    /// <summary>The one substitution: a test cannot read an SMS.</summary>
    public RecordingOtpSender Codes { get; } = new();

    /// <summary>
    /// Settings for this host only, applied over the fixture's. Read when the
    /// host is first used, so they are set before the first client is made.
    /// </summary>
    public Dictionary<string, string> Settings { get; } = new(StringComparer.Ordinal);

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.UseEnvironment(Environments.Development);

        // Warning, not the Development default of Debug.
        //
        // The host logs every request, every HTTP call to Keycloak and every
        // hosted-service transition. Across five suites that is half a million
        // characters of CI log, and the one thing it reliably buries is the
        // test failure you are looking for — which cost an afternoon of
        // guessing at a red build whose reason was in there somewhere.
        builder.UseSetting("Logging:LogLevel:Default", "Warning");
        builder.UseSetting("Logging:LogLevel:Microsoft.AspNetCore", "Warning");

        foreach (var (key, value) in _fixture.Keycloak.HostSettings)
            builder.UseSetting(key, value);

        builder.UseSetting("Data:ConnectionString", _fixture.Postgres.Options.ConnectionString);
        builder.UseSetting("Data:MigrateOnStartup", "true");

        builder.UseSetting("Messaging:HostName", IslaPayHostFixture.RabbitHost);
        // Its own queues. Two test assemblies run in parallel and both host the
        // whole application, so without this they share q.wallet.identity,
        // take turns consuming, and each misses the events the other took —
        // pointed at a different database, so the accounts open in the wrong
        // one and the test times out waiting for its own.
        builder.UseSetting("Messaging:ConsumerSuffix", _fixture.ConsumerSuffix);
        builder.UseSetting("Messaging:Port",
            (_brokerPort ?? 5672).ToString(System.Globalization.CultureInfo.InvariantCulture));
        // Short, so the test that takes the broker away does not spend its
        // life waiting for a reconnect it does not want.
        builder.UseSetting("Messaging:RecoveryInterval", "00:00:02");

        // Nothing drains on a timer here: the tests drain explicitly, so a
        // passing run is fast and a failing one is not a race. Without this,
        // the poller drains on startup and the event arrives before a test can
        // assert that it has not.
        builder.UseSetting("Outbox:PublishInBackground", "false");

        builder.UseSetting("Otp:ResendCooldown", "00:00:00");

        foreach (var (key, value) in Settings)
            builder.UseSetting(key, value);

        builder.ConfigureServices(services =>
        {
            foreach (var registration in services
                .Where(d => d.ServiceType == typeof(IOtpSender)).ToList())
            {
                services.Remove(registration);
            }

            services.AddSingleton<IOtpSender>(Codes);
        });
    }
}

/// <summary>
/// Captures one-time codes instead of sending them.
/// </summary>
/// <remarks>
/// The Identity module's own tests have one of these too. It is not shared,
/// because sharing it would put an interface from a module’s internals into
/// the common test project, and every other module’s tests would inherit a
/// dependency on Identity through it.
/// </remarks>
public sealed class RecordingOtpSender : IOtpSender
{
    private readonly ConcurrentDictionary<(OtpPurpose, string), string> _sent = new();

    public Task SendAsync(
        OtpPurpose purpose, string destination, string code, CancellationToken ct = default)
    {
        _sent[(purpose, destination)] = code;
        return Task.CompletedTask;
    }

    public string CodeFor(OtpPurpose purpose, string destination) =>
        _sent.TryGetValue((purpose, destination), out var code)
            ? code
            : throw new InvalidOperationException($"No {purpose} code was sent to {destination}.");
}

[CollectionDefinition(Name)]
public sealed class IslaPayHostDefinition : ICollectionFixture<IslaPayHostFixture>
{
    public const string Name = "islapay-host";
}
