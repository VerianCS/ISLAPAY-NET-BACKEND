using IslaPay.TestSupport;

namespace IslaPay.Identity.Tests;

/// <summary>
/// Everything the host needs to run Identity for real: a realm and a database.
/// </summary>
/// <remarks>
/// The database is here because registration now writes to the outbox, and the
/// outbox is a table. That is the cost of not doing a dual write, and it is
/// the right cost — but it does mean an identity test needs Postgres, which it
/// did not before.
/// </remarks>
public sealed class IdentityHostFixture : IAsyncLifetime
{
    public KeycloakFixture Keycloak { get; } = new();

    public PostgresFixture Postgres { get; } = new();

    public bool Available => Keycloak.Available && Postgres.Available;

    public string ConsumerSuffix { get; } = $"idt{Guid.NewGuid():N}"[..12];

    public IReadOnlyDictionary<string, string> HostSettings
    {
        get
        {
            var settings = new Dictionary<string, string>(Keycloak.HostSettings, StringComparer.Ordinal)
            {
                ["Data:ConnectionString"] = Postgres.Options.ConnectionString,
                // The host applies its modules' migrations as it starts, so
                // the schema under test is the one a deployment would get.
                ["Data:MigrateOnStartup"] = "true",
                // Its own queues: this host runs every module, Wallet included,
                // so without a suffix it competes with the end-to-end suite for
                // the same events. See MessagingOptions.ConsumerSuffix.
                ["Messaging:ConsumerSuffix"] = ConsumerSuffix,
            };
            return settings;
        }
    }

    public async Task InitializeAsync()
    {
        await Task.WhenAll(Keycloak.InitializeAsync(), Postgres.InitializeAsync());
    }

    public async Task DisposeAsync()
    {
        await Keycloak.DisposeAsync();
        Keycloak.Dispose();
        await Postgres.DisposeAsync();
    }
}

[CollectionDefinition(Name)]
public sealed class IdentityHostDefinition : ICollectionFixture<IdentityHostFixture>
{
    public const string Name = "identity-host";
}
