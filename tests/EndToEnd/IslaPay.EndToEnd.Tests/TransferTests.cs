using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using IslaPay.Identity;
using IslaPay.Identity.Contracts;
using IslaPay.Ledger.Contracts;
using IslaPay.Platform;
using IslaPay.Platform.Api;
using IslaPay.Platform.AspNet;
using IslaPay.Platform.Serialization;
using IslaPay.Wallet.Contracts;
using Microsoft.Extensions.DependencyInjection;

namespace IslaPay.EndToEnd.Tests;

/// <summary>
/// Moving money between two accounts, through the API a phone would use.
/// </summary>
/// <remarks>
/// The first endpoint that moves money, so it is also the first test of the
/// rules that guard one: a proved phone, a real destination, enough funds, and
/// a retry that does not pay twice.
/// </remarks>
[Collection(IslaPayHostDefinition.Name)]
[Trait("Category", "Integration")]
public class TransferTests
{
    private const string Password = "Correct-Horse-9";
    private static readonly JsonSerializerOptions Json = IslaPayJson.Options;

    private readonly IslaPayHostFixture _fixture;

    public TransferTests(IslaPayHostFixture fixture) => _fixture = fixture;

    [SkippableFact]
    public async Task Money_leaves_one_account_and_arrives_in_the_other()
    {
        Skip.IfNot(_fixture.Available, "No Keycloak or no Postgres reachable.");

        await using var host = _fixture.Build();
        var sender = await FundedUserAsync(host, "40.00");
        var recipient = await VerifiedUserAsync(host);

        var response = await TransferAsync(host, sender, recipient.Email, "15.00");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var movement = await Read<TransactionDto>(response);

        // The sender's own leg, from the sender's point of view.
        Assert.Equal(LedgerEntryTypes.TransferSent, movement.Type);
        Assert.Equal("-15.00", movement.Amount.ToString());
        Assert.Equal(recipient.Email, movement.Meta["to"]);

        var ledger = host.Services.GetRequiredService<ILedger>();
        Assert.Equal("25.00", (await UsdBalanceAsync(ledger, sender.UserId)).ToString());
        Assert.Equal("15.00", (await UsdBalanceAsync(ledger, recipient.UserId)).ToString());
    }

    [SkippableFact]
    public async Task The_recipient_sees_it_as_money_received()
    {
        Skip.IfNot(_fixture.Available, "No Keycloak or no Postgres reachable.");

        await using var host = _fixture.Build();
        var sender = await FundedUserAsync(host, "20.00");
        var recipient = await VerifiedUserAsync(host);

        await TransferAsync(host, sender, recipient.Email, "7.50");

        using var client = host.CreateClient();
        Authorize(client, recipient.AccessToken);
        var wallet = await Read<WalletResponse>(
            await client.GetAsync(new Uri("/v1/me/wallet", UriKind.Relative)));

        var movement = Assert.Single(wallet.Transactions.Items);

        // One posting, two readers, opposite signs — and the type follows the
        // sign rather than being recorded twice.
        Assert.Equal(LedgerEntryTypes.TransferReceived, movement.Type);
        Assert.Equal("7.50", movement.Amount.ToString());
        Assert.Equal(sender.Email, movement.Meta["from"]);
    }

    [SkippableFact]
    public async Task Sending_more_than_you_have_is_refused_with_the_figures()
    {
        Skip.IfNot(_fixture.Available, "No Keycloak or no Postgres reachable.");

        await using var host = _fixture.Build();
        var sender = await FundedUserAsync(host, "10.00");
        var recipient = await VerifiedUserAsync(host);

        var response = await TransferAsync(host, sender, recipient.Email, "10.01");

        Assert.Equal(HttpStatusCode.UnprocessableContent, response.StatusCode);
        var problem = await Read<ApiProblem>(response);

        Assert.Equal(WalletErrors.InsufficientFunds, problem.Code);

        // The client models this as InsufficientFunds(currency); without the
        // meta it cannot build the failure it is supposed to show.
        Assert.NotNull(problem.Meta);
        Assert.Equal("USD", problem.Meta!["currency"].ToString());
        Assert.Equal("10.00", problem.Meta["available"].ToString());

        // And nothing moved.
        var ledger = host.Services.GetRequiredService<ILedger>();
        Assert.Equal("10.00", (await UsdBalanceAsync(ledger, sender.UserId)).ToString());
    }

    [SkippableFact]
    public async Task An_unverified_phone_cannot_move_money()
    {
        Skip.IfNot(_fixture.Available, "No Keycloak or no Postgres reachable.");

        await using var host = _fixture.Build();

        // Registered and funded, but never proved a phone number (D11).
        var sender = await RegisterAsync(host);
        await FundAsync(host, sender.UserId, "50.00");
        var recipient = await VerifiedUserAsync(host);

        var response = await TransferAsync(host, sender, recipient.Email, "1.00");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(WalletErrors.PhoneNotVerified, (await Read<ApiProblem>(response)).Code);
    }

    [SkippableFact]
    public async Task A_verified_phone_can()
    {
        Skip.IfNot(_fixture.Available, "No Keycloak or no Postgres reachable.");

        await using var host = _fixture.Build();

        // The same account as the test above, one OTP later. Without this pair
        // the gate could be permanently closed and both tests would still pass.
        var sender = await RegisterAsync(host);
        await VerifyPhoneAsync(host, sender);
        await FundAsync(host, sender.UserId, "50.00");
        var recipient = await VerifiedUserAsync(host);

        var response = await TransferAsync(host, sender, recipient.Email, "1.00");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [SkippableFact]
    public async Task An_address_with_no_account_is_refused_the_same_way_as_a_bad_one()
    {
        Skip.IfNot(_fixture.Available, "No Keycloak or no Postgres reachable.");

        await using var host = _fixture.Build();
        var sender = await FundedUserAsync(host, "10.00");

        var unknown = await TransferAsync(
            host, sender, $"nobody-{Guid.NewGuid():N}@islapay.cu", "1.00");
        var nonsense = await TransferAsync(host, sender, "not-an-address", "1.00");

        // Telling them apart would answer "does this address have an account".
        Assert.Equal(HttpStatusCode.UnprocessableContent, unknown.StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableContent, nonsense.StatusCode);
        Assert.Equal(WalletErrors.MissingDestination, (await Read<ApiProblem>(unknown)).Code);
        Assert.Equal(WalletErrors.MissingDestination, (await Read<ApiProblem>(nonsense)).Code);
    }

    [SkippableFact]
    public async Task Sending_to_yourself_is_refused()
    {
        Skip.IfNot(_fixture.Available, "No Keycloak or no Postgres reachable.");

        await using var host = _fixture.Build();
        var sender = await FundedUserAsync(host, "10.00");

        var response = await TransferAsync(host, sender, sender.Email, "1.00");

        Assert.Equal(HttpStatusCode.UnprocessableContent, response.StatusCode);
        Assert.Equal(WalletErrors.SelfTransfer, (await Read<ApiProblem>(response)).Code);
    }

    [SkippableFact]
    public async Task Zero_and_negative_amounts_are_refused()
    {
        Skip.IfNot(_fixture.Available, "No Keycloak or no Postgres reachable.");

        await using var host = _fixture.Build();
        var sender = await FundedUserAsync(host, "10.00");
        var recipient = await VerifiedUserAsync(host);

        foreach (var amount in new[] { "0.00", "-5.00" })
        {
            var response = await TransferAsync(host, sender, recipient.Email, amount);
            Assert.Equal(HttpStatusCode.UnprocessableContent, response.StatusCode);
            Assert.Equal(WalletErrors.InvalidAmount, (await Read<ApiProblem>(response)).Code);
        }
    }

    // ------------------------------------------------------------------ idempotency

    [SkippableFact]
    public async Task A_retry_with_the_same_key_pays_once()
    {
        Skip.IfNot(_fixture.Available, "No Keycloak or no Postgres reachable.");

        await using var host = _fixture.Build();
        var sender = await FundedUserAsync(host, "30.00");
        var recipient = await VerifiedUserAsync(host);

        var key = Guid.NewGuid().ToString("N");

        // The client did not hear the first answer and asked again. This is the
        // ordinary case on a bad connection, not an edge one.
        var first = await TransferAsync(host, sender, recipient.Email, "12.00", key);
        var second = await TransferAsync(host, sender, recipient.Email, "12.00", key);

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        Assert.Equal(
            await first.Content.ReadAsStringAsync(),
            await second.Content.ReadAsStringAsync());

        var ledger = host.Services.GetRequiredService<ILedger>();
        Assert.Equal("18.00", (await UsdBalanceAsync(ledger, sender.UserId)).ToString());
        Assert.Equal("12.00", (await UsdBalanceAsync(ledger, recipient.UserId)).ToString());
    }

    [SkippableFact]
    public async Task The_same_key_for_a_different_request_is_a_client_bug()
    {
        Skip.IfNot(_fixture.Available, "No Keycloak or no Postgres reachable.");

        await using var host = _fixture.Build();
        var sender = await FundedUserAsync(host, "30.00");
        var recipient = await VerifiedUserAsync(host);

        var key = Guid.NewGuid().ToString("N");
        await TransferAsync(host, sender, recipient.Email, "5.00", key);

        // Same key, different amount. Replaying the first answer would tell the
        // user the second transfer succeeded, and running it would defeat the
        // key. Neither: the client is told it has a bug.
        var response = await TransferAsync(host, sender, recipient.Email, "6.00", key);

        Assert.Equal(HttpStatusCode.UnprocessableContent, response.StatusCode);
        Assert.Equal(
            PlatformErrors.IdempotencyKeyReuse, (await Read<ApiProblem>(response)).Code);

        var ledger = host.Services.GetRequiredService<ILedger>();
        Assert.Equal("25.00", (await UsdBalanceAsync(ledger, sender.UserId)).ToString());
    }

    [SkippableFact]
    public async Task A_refusal_is_replayed_rather_than_re_run()
    {
        Skip.IfNot(_fixture.Available, "No Keycloak or no Postgres reachable.");

        await using var host = _fixture.Build();
        var sender = await FundedUserAsync(host, "1.00");
        var recipient = await VerifiedUserAsync(host);

        var key = Guid.NewGuid().ToString("N");
        var first = await TransferAsync(host, sender, recipient.Email, "100.00", key);
        var second = await TransferAsync(host, sender, recipient.Email, "100.00", key);

        // The answer to the same wrong question is the same wrong answer, and
        // the server does not do the work twice to produce it.
        Assert.Equal(HttpStatusCode.UnprocessableContent, first.StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableContent, second.StatusCode);
        Assert.Equal(
            await first.Content.ReadAsStringAsync(),
            await second.Content.ReadAsStringAsync());
    }

    [SkippableFact]
    public async Task A_transfer_without_a_key_is_refused()
    {
        Skip.IfNot(_fixture.Available, "No Keycloak or no Postgres reachable.");

        await using var host = _fixture.Build();
        var sender = await FundedUserAsync(host, "10.00");
        var recipient = await VerifiedUserAsync(host);

        using var client = host.CreateClient();
        Authorize(client, sender.AccessToken);

        var response = await client.PostAsJsonAsync(
            "/v1/transfers",
            new TransferRequest(Money.Parse("1.00", Currency.Usd), recipient.Email),
            Json);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(
            PlatformErrors.MalformedRequest, (await Read<ApiProblem>(response)).Code);
    }

    [SkippableFact]
    public async Task One_users_key_cannot_replay_anothers_answer()
    {
        Skip.IfNot(_fixture.Available, "No Keycloak or no Postgres reachable.");

        await using var host = _fixture.Build();
        var first = await FundedUserAsync(host, "20.00");
        var second = await FundedUserAsync(host, "20.00");
        var recipient = await VerifiedUserAsync(host);

        // The same key, chosen by two people. Scoping it to the caller is what
        // stops one of them being handed the other's response.
        var key = Guid.NewGuid().ToString("N");
        await TransferAsync(host, first, recipient.Email, "3.00", key);
        var response = await TransferAsync(host, second, recipient.Email, "4.00", key);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var ledger = host.Services.GetRequiredService<ILedger>();
        Assert.Equal("7.00", (await UsdBalanceAsync(ledger, recipient.UserId)).ToString());
    }

    // ------------------------------------------------------------------ the event

    [SkippableFact]
    public async Task The_transfer_is_announced_with_the_money_not_after_it()
    {
        Skip.IfNot(_fixture.Available, "No Keycloak or no Postgres reachable.");

        await using var host = _fixture.Build();
        var sender = await FundedUserAsync(host, "10.00");
        var recipient = await VerifiedUserAsync(host);

        var transfer = await TransferAsync(host, sender, recipient.Email, "4.00");

        // Checked before looking for the event, so a transfer that failed
        // reports itself rather than being reported as a missing announcement.
        Assert.True(
            transfer.IsSuccessStatusCode,
            $"transfer returned {(int)transfer.StatusCode}: "
            + await transfer.Content.ReadAsStringAsync());

        var payload = await PendingEventPayloadAsync(host, WalletEvents.TransferCompleted);
        Assert.NotNull(payload);

        var announced = JsonSerializer.Deserialize<TransferCompleted>(payload!, Json)!;
        Assert.Equal(sender.UserId, announced.FromUserId);
        Assert.Equal(recipient.UserId, announced.ToUserId);
        Assert.Equal("4.00", announced.Amount.ToString());
        Assert.NotEqual(Guid.Empty, announced.PostingId);
    }

    [SkippableFact]
    public async Task A_refused_transfer_announces_nothing()
    {
        Skip.IfNot(_fixture.Available, "No Keycloak or no Postgres reachable.");

        await using var host = _fixture.Build();
        var sender = await FundedUserAsync(host, "1.00");
        var recipient = await VerifiedUserAsync(host);

        await DrainPendingAsync(host);
        await TransferAsync(host, sender, recipient.Email, "500.00");

        // The event and the entries are written in one transaction, so a
        // posting that rolled back cannot have left an announcement behind.
        Assert.Null(await PendingEventPayloadAsync(host, WalletEvents.TransferCompleted));
    }

    // ------------------------------------------------------------------ helpers

    private sealed record Account(string UserId, string Email, string Phone, string AccessToken);

    private static async Task<Account> RegisterAsync(IslaPayHost host)
    {
        using var client = host.CreateClient();

        var id = Guid.NewGuid().ToString("N")[..10];
        var email = $"ana-{id}@islapay.cu";
        var phone = "+53" + Random.Shared.NextInt64(500_000_000, 599_999_999)
            .ToString(System.Globalization.CultureInfo.InvariantCulture);

        var response = await client.PostAsJsonAsync(
            "/v1/auth/register",
            new RegisterRequest("Ana Pérez", email, phone, Password), Json);

        var session = await Read<AuthSessionResponse>(response);
        return new Account(session.User.Id, email, phone, session.Tokens.AccessToken);
    }

    private static async Task VerifyPhoneAsync(IslaPayHost host, Account account)
    {
        using var client = host.CreateClient();
        Authorize(client, account.AccessToken);

        var code = host.Codes.CodeFor(OtpPurpose.PhoneVerification, account.Phone);
        var response = await client.PostAsJsonAsync(
            "/v1/auth/otp/verify", new OtpVerifyRequest(account.Phone, code), Json);

        Assert.True(response.IsSuccessStatusCode,
            $"verify returned {(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
    }

    private static async Task<Account> VerifiedUserAsync(IslaPayHost host)
    {
        var account = await RegisterAsync(host);
        await VerifyPhoneAsync(host, account);
        return account;
    }

    private static async Task<Account> FundedUserAsync(IslaPayHost host, string amount)
    {
        var account = await VerifiedUserAsync(host);
        await FundAsync(host, account.UserId, amount);
        return account;
    }

    /// <summary>
    /// Puts money in an account the only way that exists today.
    /// </summary>
    /// <remarks>
    /// There is no deposit endpoint yet, so the test posts a settlement
    /// against the platform's float through the ledger's own contract — the
    /// same call a recharge will make when it exists.
    /// </remarks>
    private static async Task FundAsync(IslaPayHost host, string userId, string amount)
    {
        var money = Money.Parse(amount, Currency.Usd);
        await host.Services.GetRequiredService<ILedger>().PostAsync(new PostingRequest(
            Kind: "settlement",
            Legs:
            [
                new PostingLeg(AccountRef.User(userId, Currency.Usd), money),
                new PostingLeg(AccountRef.CashFloat(Currency.Usd), -money),
            ],
            Metadata: new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["method"] = "test",
            }));
    }

    private static async Task<HttpResponseMessage> TransferAsync(
        IslaPayHost host, Account sender, string destination, string amount, string? key = null)
    {
        using var client = host.CreateClient();
        Authorize(client, sender.AccessToken);

        using var request = new HttpRequestMessage(HttpMethod.Post, "/v1/transfers")
        {
            Content = new StringContent(
                JsonSerializer.Serialize(
                    new TransferRequest(Money.Parse(amount, Currency.Usd), destination), Json),
                Encoding.UTF8,
                "application/json"),
        };
        request.Headers.Add(
            IdempotencyMiddleware.HeaderName, key ?? Guid.NewGuid().ToString("N"));

        return await client.SendAsync(request);
    }

    private static void Authorize(HttpClient client, string accessToken) =>
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", accessToken);

    private static async Task<Money> UsdBalanceAsync(ILedger ledger, string userId) =>
        (await ledger.BalancesAsync(userId)).Single(b => b.Currency == Currency.Usd).Balance;

    private static async Task<T> Read<T>(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync();
        return JsonSerializer.Deserialize<T>(body, Json)
            ?? throw new InvalidOperationException($"Could not read a {typeof(T).Name} from: {body}");
    }

    private static async Task<string?> PendingEventPayloadAsync(
        IslaPayHost host, string routingKey)
    {
        var database = host.Services.GetRequiredService<Platform.Data.IDatabase>();
        await using var connection = await database.OpenAsync();
        await using var command = new Npgsql.NpgsqlCommand("""
            SELECT payload::text FROM messaging.outbox
            WHERE routing_key = @key AND published_at IS NULL
            ORDER BY id DESC LIMIT 1;
            """, connection);
        command.Parameters.AddWithValue("key", routingKey);

        return await command.ExecuteScalarAsync() as string;
    }

    /// <summary>Clears the backlog so a later assertion sees only new rows.</summary>
    private static async Task DrainPendingAsync(IslaPayHost host)
    {
        await host.Services.GetRequiredService<Platform.Messaging.MessagingTopology>()
            .Declared.WaitAsync(TimeSpan.FromSeconds(30));
        await host.Services.GetRequiredService<Platform.Messaging.OutboxPublisher>()
            .DrainOnceAsync();
    }
}
