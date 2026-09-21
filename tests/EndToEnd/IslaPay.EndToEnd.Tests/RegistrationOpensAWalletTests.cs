using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using IslaPay.Identity.Contracts;
using IslaPay.Ledger.Contracts;
using IslaPay.Platform.Messaging;
using IslaPay.Platform.Serialization;
using IslaPay.Wallet.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace IslaPay.EndToEnd.Tests;

/// <summary>
/// Registering a user opens their wallet — the first journey that crosses a
/// module boundary.
/// </summary>
/// <remarks>
/// Identity creates the account in Keycloak and writes <c>user.registered.v1</c>
/// to the outbox. The outbox publishes to RabbitMQ. Wallet consumes it and
/// opens the ledger accounts. Nothing in that sentence is mocked here.
/// </remarks>
[Collection(IslaPayHostDefinition.Name)]
[Trait("Category", "Integration")]
public class RegistrationOpensAWalletTests
{
    private const string Password = "Correct-Horse-9";
    private static readonly JsonSerializerOptions Json = IslaPayJson.Options;

    private readonly IslaPayHostFixture _fixture;

    public RegistrationOpensAWalletTests(IslaPayHostFixture fixture) => _fixture = fixture;

    [SkippableFact]
    public async Task The_event_opens_the_accounts_before_anyone_looks_at_the_wallet()
    {
        Skip.IfNot(_fixture.Available, "No Keycloak or no Postgres reachable.");

        await using var host = _fixture.Build();
        using var client = host.CreateClient();

        var session = await RegisterAsync(client);
        var ledger = host.Services.GetRequiredService<ILedger>();

        // Nothing has touched the wallet endpoint, so the lazy path cannot be
        // what opens these. If they appear, the event is what did it.
        Assert.Empty(await ledger.BalancesAsync(session.User.Id));

        await DrainOutboxAsync(host);
        var balances = await EventuallyAsync(
            () => ledger.BalancesAsync(session.User.Id),
            found => found.Count == 3);

        Assert.Equal(3, balances.Count);
        Assert.All(balances, b => Assert.Equal(0, b.Balance.MinorUnits));
    }

    [SkippableFact]
    public async Task A_new_user_gets_a_wallet_even_with_the_bus_down()
    {
        Skip.IfNot(_fixture.Available, "No Keycloak or no Postgres reachable.");

        // Nothing listens on this port, so the event cannot possibly arrive.
        // Keycloak owns the account and this database cannot join the
        // transaction that created it, so an event lost here is a user with no
        // wallet — unless the read path opens the accounts too. This is the
        // test of that claim.
        await using var host = _fixture.Build(brokerPort: 5673);
        using var client = host.CreateClient();

        var session = await RegisterAsync(client);
        var ledger = host.Services.GetRequiredService<ILedger>();

        Assert.Empty(await ledger.BalancesAsync(session.User.Id));

        Authorize(client, session);
        var wallet = await ReadWalletAsync(client);

        Assert.Equal(3, wallet.Accounts.Count);
        Assert.Equal(3, (await ledger.BalancesAsync(session.User.Id)).Count);
    }

    [SkippableFact]
    public async Task The_wallet_response_is_the_shape_the_client_parses()
    {
        Skip.IfNot(_fixture.Available, "No Keycloak or no Postgres reachable.");

        await using var host = _fixture.Build();
        using var client = host.CreateClient();

        var session = await RegisterAsync(client);
        Authorize(client, session);

        var response = await client.GetAsync(new Uri("/v1/me/wallet", UriKind.Relative));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadAsStringAsync();
        var wallet = JsonSerializer.Deserialize<WalletResponse>(body, Json)!;

        Assert.Equal(
            ["EISLA", "USDC", "USDT"],
            wallet.Accounts.Select(a => a.Currency).Order(StringComparer.Ordinal));

        // Every ordered pair, keyed by the currency codes themselves.
        //
        // These were typed out, and went on saying `USD_USDC` after USD became
        // E-ISLA with nothing failing: the client falls back to a rate of one
        // for a key it cannot find, which is indistinguishable from parity
        // until the day parity ends. Only three of the six pairs were listed,
        // so half the conversions the app offers were quoted from that same
        // silent default.
        Assert.Equal(
            [
                "EISLA_USDC", "EISLA_USDT",
                "USDC_EISLA", "USDC_USDT",
                "USDT_EISLA", "USDT_USDC",
            ],
            wallet.Rates.Keys.Order(StringComparer.Ordinal));

        // Money is an object with a string amount, never a bare number — the
        // one thing the client cannot recover from.
        Assert.Contains("\"amount\":\"0.00\"", body, StringComparison.Ordinal);
        Assert.DoesNotContain("\"balance\":0", body, StringComparison.Ordinal);

        // No card yet, so no last four digits invented to fill the field.
        Assert.DoesNotContain("cardLast4", body, StringComparison.Ordinal);

        // A brand-new wallet has no history, and that is an empty list rather
        // than a missing one.
        Assert.Empty(wallet.Transactions.Items);
        Assert.Null(wallet.Transactions.NextCursor);
    }

    [SkippableFact]
    public async Task The_wallet_is_the_callers_own_and_needs_a_token()
    {
        Skip.IfNot(_fixture.Available, "No Keycloak or no Postgres reachable.");

        await using var host = _fixture.Build();
        using var client = host.CreateClient();

        // There is no user id anywhere in the route, so there is no parameter
        // to forget to check.
        var response = await client.GetAsync(new Uri("/v1/me/wallet", UriKind.Relative));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [SkippableFact]
    public async Task A_published_row_is_marked_and_never_published_twice()
    {
        Skip.IfNot(_fixture.Available, "No Keycloak or no Postgres reachable.");

        await using var host = _fixture.Build();
        using var client = host.CreateClient();

        await RegisterAsync(client);

        var sent = await DrainOutboxAsync(host);
        Assert.True(sent >= 1);

        // The second drain finds nothing: the row is marked, so a restart or a
        // second instance does not re-announce every user who ever registered.
        Assert.Equal(0, await DrainOutboxAsync(host));
        Assert.Equal(0, await PendingOutboxRowsAsync(host));
    }

    [SkippableFact]
    public async Task Redelivering_the_event_opens_nothing_twice()
    {
        Skip.IfNot(_fixture.Available, "No Keycloak or no Postgres reachable.");

        await using var host = _fixture.Build();
        using var client = host.CreateClient();

        var session = await RegisterAsync(client);
        await DrainOutboxAsync(host);
        await EventuallyAsync(
            () => host.Services.GetRequiredService<ILedger>().BalancesAsync(session.User.Id),
            found => found.Count == 3);

        // At-least-once delivery is not a caveat to apologise for, it is the
        // contract. The handler is called again with the same event, exactly as
        // the broker would after a failed ack.
        using var scope = host.Services.CreateScope();
        var handler = scope.ServiceProvider.GetRequiredService<Wallet.UserRegisteredHandler>();
        var envelope = new MessageEnvelope(
            IdentityEvents.UserRegistered,
            JsonSerializer.SerializeToUtf8Bytes(
                new UserRegistered(session.User.Id, session.User.Email, session.User.Phone,
                    DateTimeOffset.UtcNow),
                Json),
            CorrelationId: session.User.Id,
            MessageId: Guid.NewGuid().ToString("N"));

        await handler.HandleAsync(envelope, CancellationToken.None);

        var balances = await host.Services.GetRequiredService<ILedger>()
            .BalancesAsync(session.User.Id);
        Assert.Equal(3, balances.Count);
    }

    // ------------------------------------------------------------------ helpers

    private static async Task<AuthSessionResponse> RegisterAsync(HttpClient client)
    {
        var id = Guid.NewGuid().ToString("N")[..10];
        var request = new RegisterRequest(
            Name: "Ana Pérez",
            Email: $"ana-{id}@islapay.cu",
            Phone: "+53" + Random.Shared.NextInt64(500_000_000, 599_999_999)
                .ToString(System.Globalization.CultureInfo.InvariantCulture),
            Password: Password);

        var response = await client.PostAsJsonAsync("/v1/auth/register", request, Json);
        var body = await response.Content.ReadAsStringAsync();

        Assert.True(response.IsSuccessStatusCode, $"register returned {(int)response.StatusCode}: {body}");
        return JsonSerializer.Deserialize<AuthSessionResponse>(body, Json)!;
    }

    private static void Authorize(HttpClient client, AuthSessionResponse session) =>
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", session.Tokens.AccessToken);

    private static async Task<WalletResponse> ReadWalletAsync(HttpClient client)
    {
        var response = await client.GetAsync(new Uri("/v1/me/wallet", UriKind.Relative));
        var body = await response.Content.ReadAsStringAsync();

        Assert.True(response.IsSuccessStatusCode, $"wallet returned {(int)response.StatusCode}: {body}");
        return JsonSerializer.Deserialize<WalletResponse>(body, Json)!;
    }

    /// <summary>
    /// Drains explicitly rather than waiting for the poller's timer, once the
    /// queues exist.
    /// </summary>
    /// <remarks>
    /// Waiting for the topology is not test ceremony. A topic exchange drops
    /// what it cannot route, so publishing before the consumer's queue is
    /// bound loses the message silently — which is what made this test fail in
    /// CI and pass on a faster machine. The background poller waits on the
    /// same signal for the same reason.
    /// </remarks>
    private static async Task<int> DrainOutboxAsync(IslaPayHost host)
    {
        await host.Services.GetRequiredService<MessagingTopology>()
            .Declared.WaitAsync(TimeSpan.FromSeconds(30));

        return await host.Services.GetRequiredService<OutboxPublisher>().DrainOnceAsync();
    }

    private static async Task<long> PendingOutboxRowsAsync(IslaPayHost host)
    {
        var database = host.Services.GetRequiredService<Platform.Data.IDatabase>();
        await using var connection = await database.OpenAsync();
        await using var command = new NpgsqlCommand(
            "SELECT count(*) FROM messaging.outbox WHERE published_at IS NULL;", connection);
        return (long)(await command.ExecuteScalarAsync())!;
    }

    /// <summary>
    /// Polls until a condition holds, or gives up.
    /// </summary>
    /// <remarks>
    /// Consuming is asynchronous by design, so a test that asserts immediately
    /// after publishing is asserting on a race. Polling with a deadline is
    /// honest about that; a fixed sleep would be slow when it passes and flaky
    /// when it does not.
    /// </remarks>
    private static async Task<T> EventuallyAsync<T>(
        Func<Task<T>> read, Func<T, bool> until, int timeoutSeconds = 15)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(timeoutSeconds);
        T last;

        do
        {
            last = await read();
            if (until(last)) return last;
            await Task.Delay(100);
        }
        while (DateTimeOffset.UtcNow < deadline);

        Assert.Fail($"The condition never held within {timeoutSeconds}s. Last value: {last}");
        return last;
    }
}
