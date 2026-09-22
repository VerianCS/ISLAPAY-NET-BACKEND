using IslaPay.TestSupport;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using IslaPay.Identity;
using IslaPay.Identity.Contracts;
using IslaPay.Ledger.Contracts;
using IslaPay.P2P;
using IslaPay.P2P.Contracts;
using IslaPay.Platform;
using IslaPay.Platform.Api;
using IslaPay.Platform.AspNet;
using IslaPay.Platform.Serialization;
using Microsoft.Extensions.DependencyInjection;

namespace IslaPay.EndToEnd.Tests;

/// <summary>
/// Money leaving IslaPay and arriving in it, through the API a phone and an
/// operator would use.
/// </summary>
/// <remarks>
/// The module's own tests drive the state machine against a fake ledger. These
/// check what a fake cannot: that a posting spanning two currencies really
/// balances in a real double-entry ledger, that the CUP escrow really empties,
/// and that the operator endpoints really refuse somebody without the role.
/// </remarks>
[Collection(IslaPayHostDefinition.Name)]
[Trait("Category", "Integration")]
public class P2PTests
{
    private const string Password = "Correct-Horse-9";
    private const string Rail = "cup_transfermovil";
    private static readonly JsonSerializerOptions Json = IslaPayJson.Create(TestCurrencies.Scales);

    private readonly IslaPayHostFixture _fixture;

    public P2PTests(IslaPayHostFixture fixture) => _fixture = fixture;

    [SkippableFact]
    public async Task A_sale_debits_the_wallet_and_leaves_the_local_money_in_escrow()
    {
        Skip.IfNot(_fixture.Available, "No Keycloak or no Postgres reachable.");

        await using var host = _fixture.Build();
        var operador = await OperatorAsync(host);
        await OpenTheMarketAsync(host, operador);

        var user = await FundedUserAsync(host, "200.00");
        await FundTheDeskAsync(host, TestCurrencies.Cup, "5000000.00");

        var escrowBefore = await PlatformBalanceAsync(host, AccountRef.Escrow(TestCurrencies.Cup));

        var trade = await Read<P2PTradeDto>(
            await TradeAsync(host, user, P2PSide.Sell, "100.00"));

        Assert.Equal(P2PTradeStatuses.AwaitingPayout, trade.Status);
        Assert.Equal("11880.00", trade.Local.ToString());

        var ledger = host.Services.GetRequiredService<ILedger>();
        Assert.Equal("100.00", (await EIslaBalanceAsync(ledger, user.UserId)).ToString());

        // The obligation is a real liability in the ledger, not a flag on a
        // row: this is what TestCurrencies.Cup exists for.
        var escrowAfter = await PlatformBalanceAsync(host, AccountRef.Escrow(TestCurrencies.Cup));
        Assert.Equal(1188000, escrowAfter.MinorUnits - escrowBefore.MinorUnits);
    }

    [SkippableFact]
    public async Task Confirming_the_payout_empties_the_escrow()
    {
        Skip.IfNot(_fixture.Available, "No Keycloak or no Postgres reachable.");

        await using var host = _fixture.Build();
        var operador = await OperatorAsync(host);
        await OpenTheMarketAsync(host, operador);

        var user = await FundedUserAsync(host, "200.00");
        await FundTheDeskAsync(host, TestCurrencies.Cup, "5000000.00");

        var before = await PlatformBalanceAsync(host, AccountRef.Escrow(TestCurrencies.Cup));
        var trade = await Read<P2PTradeDto>(await TradeAsync(host, user, P2PSide.Sell, "50.00"));

        var paid = await Read<P2PTradeDto>(await PostAsync(
            host, operador, $"/v1/admin/p2p/trades/{trade.Id}/paid",
            new P2PSettleRequest("TM-55512")));

        Assert.Equal(P2PTradeStatuses.Completed, paid.Status);
        Assert.Equal(before, await PlatformBalanceAsync(host, AccountRef.Escrow(TestCurrencies.Cup)));

        // And the rail's mirror carries what was sent, which is the figure an
        // operator reconciles a bank statement against.
        Assert.Equal(
            "5940.00",
            (await PlatformBalanceAsync(host, AccountRef.External(Rail, TestCurrencies.Cup))).ToString());
    }

    [SkippableFact]
    public async Task A_failed_payout_puts_the_seller_back_where_they_started()
    {
        Skip.IfNot(_fixture.Available, "No Keycloak or no Postgres reachable.");

        await using var host = _fixture.Build();
        var operador = await OperatorAsync(host);
        await OpenTheMarketAsync(host, operador);

        var user = await FundedUserAsync(host, "80.00");
        await FundTheDeskAsync(host, TestCurrencies.Cup, "5000000.00");

        var trade = await Read<P2PTradeDto>(await TradeAsync(host, user, P2PSide.Sell, "80.00"));

        var refunded = await Read<P2PTradeDto>(await PostAsync(
            host, operador, $"/v1/admin/p2p/trades/{trade.Id}/failed",
            new P2PFailRequest("El teléfono no está registrado en Transfermóvil.")));

        Assert.Equal(P2PTradeStatuses.Refunded, refunded.Status);

        // Down to the cent, fee included.
        var ledger = host.Services.GetRequiredService<ILedger>();
        Assert.Equal("80.00", (await EIslaBalanceAsync(ledger, user.UserId)).ToString());
    }

    [SkippableFact]
    public async Task A_purchase_credits_nothing_until_the_operator_confirms()
    {
        Skip.IfNot(_fixture.Available, "No Keycloak or no Postgres reachable.");

        await using var host = _fixture.Build();
        var operador = await OperatorAsync(host);
        await OpenTheMarketAsync(host, operador);

        var user = await VerifiedUserAsync(host);
        await FundTheDeskAsync(host, TestCurrencies.EIsla, "10000.00");

        var trade = await Read<P2PTradeDto>(await TradeAsync(host, user, P2PSide.Buy, "100.00"));

        Assert.Equal(P2PTradeStatuses.AwaitingPayment, trade.Status);
        Assert.Equal("12500.00", trade.Local.ToString());
        Assert.NotNull(trade.Reference);

        var ledger = host.Services.GetRequiredService<ILedger>();
        Assert.Equal("0.00", (await EIslaBalanceAsync(ledger, user.UserId)).ToString());

        var credited = await Read<P2PTradeDto>(await PostAsync(
            host, operador, $"/v1/admin/p2p/trades/{trade.Id}/received",
            new P2PSettleRequest("TM-31415")));

        Assert.Equal(P2PTradeStatuses.Completed, credited.Status);
        Assert.Equal("99.00", (await EIslaBalanceAsync(ledger, user.UserId)).ToString());
    }

    [SkippableFact]
    public async Task The_operator_endpoints_refuse_an_ordinary_account()
    {
        Skip.IfNot(_fixture.Available, "No Keycloak or no Postgres reachable.");

        await using var host = _fixture.Build();
        var operador = await OperatorAsync(host);
        await OpenTheMarketAsync(host, operador);

        var user = await FundedUserAsync(host, "60.00");
        await FundTheDeskAsync(host, TestCurrencies.Cup, "5000000.00");

        var trade = await Read<P2PTradeDto>(await TradeAsync(host, user, P2PSide.Sell, "60.00"));

        // A customer confirming their own payout would be a way to be paid
        // twice: once in CUP that never arrived, once by saying it did.
        var queue = await GetAsync(host, user, "/v1/admin/p2p/queue");
        Assert.Equal(HttpStatusCode.Forbidden, queue.StatusCode);

        var settle = await PostAsync(
            host, user, $"/v1/admin/p2p/trades/{trade.Id}/paid",
            new P2PSettleRequest("TM-1"));
        Assert.Equal(HttpStatusCode.Forbidden, settle.StatusCode);

        // And the refusal carries a code the client can switch on.
        Assert.Equal(PlatformErrors.Forbidden, (await Read<ApiProblem>(settle)).Code);
    }

    [SkippableFact]
    public async Task A_trade_is_refused_when_the_desk_cannot_cover_it()
    {
        Skip.IfNot(_fixture.Available, "No Keycloak or no Postgres reachable.");

        await using var host = _fixture.Build();
        var operador = await OperatorAsync(host);
        await OpenTheMarketAsync(host, operador);

        var user = await FundedUserAsync(host, "500.00");

        // Emptied rather than simply not filled. Every test in this collection
        // shares one database and the settlement fund is global, so a sibling
        // that topped it up would otherwise decide whether this one passes.
        await DrainTheDeskAsync(host, TestCurrencies.Cup);

        var response = await TradeAsync(host, user, P2PSide.Sell, "400.00");

        Assert.Equal(HttpStatusCode.UnprocessableContent, response.StatusCode);
        var problem = await Read<ApiProblem>(response);

        // The settlement fund may go negative, so nothing in the ledger would
        // have stopped this. The check is the only thing that does.
        Assert.Equal(P2PErrors.FundUnavailable, problem.Code);

        var ledger = host.Services.GetRequiredService<ILedger>();
        Assert.Equal("500.00", (await EIslaBalanceAsync(ledger, user.UserId)).ToString());
    }

    [SkippableFact]
    public async Task A_retry_with_the_same_key_opens_one_trade()
    {
        Skip.IfNot(_fixture.Available, "No Keycloak or no Postgres reachable.");

        await using var host = _fixture.Build();
        var operador = await OperatorAsync(host);
        await OpenTheMarketAsync(host, operador);

        var user = await FundedUserAsync(host, "100.00");
        await FundTheDeskAsync(host, TestCurrencies.Cup, "5000000.00");

        var key = Guid.NewGuid().ToString("N");
        var first = await Read<P2PTradeDto>(
            await TradeAsync(host, user, P2PSide.Sell, "60.00", key));
        var second = await Read<P2PTradeDto>(
            await TradeAsync(host, user, P2PSide.Sell, "60.00", key));

        Assert.Equal(first.Id, second.Id);

        // The second would have overdrawn a 100 balance by 20.
        var ledger = host.Services.GetRequiredService<ILedger>();
        Assert.Equal("40.00", (await EIslaBalanceAsync(ledger, user.UserId)).ToString());
    }

    [SkippableFact]
    public async Task The_sale_shows_up_in_the_wallet_and_the_queue()
    {
        Skip.IfNot(_fixture.Available, "No Keycloak or no Postgres reachable.");

        await using var host = _fixture.Build();
        var operador = await OperatorAsync(host);
        await OpenTheMarketAsync(host, operador);

        var user = await FundedUserAsync(host, "120.00");
        await FundTheDeskAsync(host, TestCurrencies.Cup, "5000000.00");

        var trade = await Read<P2PTradeDto>(await TradeAsync(host, user, P2PSide.Sell, "120.00"));

        var queue = await Read<List<P2PQueueItemDto>>(
            await GetAsync(host, operador, "/v1/admin/p2p/queue"));

        var waiting = Assert.Single(queue, q => q.Id == trade.Id);
        Assert.Equal(trade.Reference, waiting.Reference);
        Assert.Equal("14256.00", waiting.Local.ToString());

        var wallet = await Read<Wallet.Contracts.WalletResponse>(
            await GetAsync(host, user, "/v1/me/wallet"));

        Assert.Contains(wallet.Transactions.Items, t => t.Amount.ToString() == "-120.00");
    }

    // ---------------------------------------------------------------- helpers

    private sealed record Account(string UserId, string Email, string Phone, string AccessToken);

    /// <summary>An account with the operator role, and a token that carries it.</summary>
    private async Task<Account> OperatorAsync(IslaPayHost host)
    {
        var account = await VerifiedUserAsync(host);

        await _fixture.Keycloak.GrantRealmRoleAsync(account.UserId, P2PModule.OperatorRole);

        // Signed in again: roles are baked into a token when it is issued, and
        // the one from registration predates the grant.
        using var client = host.CreateClient();
        var response = await client.PostAsJsonAsync(
            "/v1/auth/login", new LoginRequest(account.Email, Password), Json);
        response.EnsureSuccessStatusCode();

        var session = (await response.Content.ReadFromJsonAsync<AuthSessionResponse>(Json))!;
        return account with { AccessToken = session.Tokens.AccessToken };
    }

    /// <summary>Publishes both rates and switches the rail on.</summary>
    private static async Task OpenTheMarketAsync(IslaPayHost host, Account operador)
    {
        foreach (var (side, rate) in new[] { (P2PSide.Sell, "120"), (P2PSide.Buy, "125") })
        {
            using var client = host.CreateClient();
            Authorize(client, operador.AccessToken);
            var response = await client.PutAsJsonAsync(
                "/v1/admin/p2p/rates", new P2PRateUpdate(Rail, side, "EISLA", rate), Json);

            Assert.True(response.IsSuccessStatusCode,
                $"set {side} rate: {(int)response.StatusCode} {await response.Content.ReadAsStringAsync()}");
        }

        using var toggle = host.CreateClient();
        Authorize(toggle, operador.AccessToken);
        var opened = await toggle.PutAsync(
            new Uri($"/v1/admin/p2p/methods/{Rail}/available?value=true", UriKind.Relative), null);

        Assert.True(opened.IsSuccessStatusCode,
            $"open the rail: {(int)opened.StatusCode} {await opened.Content.ReadAsStringAsync()}");
    }

    private static Task<HttpResponseMessage> TradeAsync(
        IslaPayHost host, Account user, P2PSide side, string amount, string? key = null) =>
        PostAsync(
            host, user, "/v1/p2p/trades",
            new P2PTradeRequest(side, Money.Parse(amount, TestCurrencies.EIsla), Rail), key);

    private static async Task<HttpResponseMessage> PostAsync(
        IslaPayHost host, Account actor, string path, object body, string? key = null)
    {
        using var client = host.CreateClient();
        Authorize(client, actor.AccessToken);

        using var request = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = new StringContent(
                JsonSerializer.Serialize(body, Json), Encoding.UTF8, "application/json"),
        };
        request.Headers.Add(IdempotencyMiddleware.HeaderName, key ?? Guid.NewGuid().ToString("N"));

        return await client.SendAsync(request);
    }

    private static async Task<HttpResponseMessage> GetAsync(
        IslaPayHost host, Account actor, string path)
    {
        using var client = host.CreateClient();
        Authorize(client, actor.AccessToken);
        return await client.GetAsync(new Uri(path, UriKind.Relative));
    }

    /// <summary>
    /// Puts local currency behind the desk.
    /// </summary>
    /// <remarks>
    /// The fund's CUP has to come from somewhere, and in production that
    /// somewhere is a Cuban bank account an operator topped up. There is no
    /// endpoint for it yet, so this posts the settlement directly — the same
    /// call a treasury endpoint will make when it exists.
    /// </remarks>
    private static async Task FundTheDeskAsync(IslaPayHost host, Currency currency, string amount)
    {
        var money = Money.Parse(amount, currency);
        await host.Services.GetRequiredService<ILedger>().PostAsync(new PostingRequest(
            Kind: "settlement",
            Legs:
            [
                new PostingLeg(AccountRef.SettlementFund(currency), money),
                new PostingLeg(AccountRef.CashFloat(currency), -money),
            ],
            Metadata: new Dictionary<string, string>(StringComparer.Ordinal) { ["method"] = "test" }));
    }

    /// <summary>Moves everything the desk holds back out to the float.</summary>
    private static async Task DrainTheDeskAsync(IslaPayHost host, Currency currency)
    {
        var ledger = host.Services.GetRequiredService<ILedger>();
        var held = await ledger.BalanceOfAsync(AccountRef.SettlementFund(currency));

        if (!held.IsPositive) return;

        await ledger.PostAsync(new PostingRequest(
            Kind: "settlement",
            Legs:
            [
                new PostingLeg(AccountRef.SettlementFund(currency), -held),
                new PostingLeg(AccountRef.CashFloat(currency), held),
            ]));
    }

    private static Task<Money> PlatformBalanceAsync(IslaPayHost host, AccountRef account) =>
        host.Services.GetRequiredService<ILedger>().BalanceOfAsync(account);

    private static async Task<Account> VerifiedUserAsync(IslaPayHost host)
    {
        using var client = host.CreateClient();
        var email = $"p2p-{Guid.NewGuid():N}@example.test";
        var phone = $"+5355{Random.Shared.Next(100000, 999999)}";

        var response = await client.PostAsJsonAsync(
            "/v1/auth/register",
            new RegisterRequest("Ana Pérez", email, phone, Password), Json);
        response.EnsureSuccessStatusCode();

        var session = (await response.Content.ReadFromJsonAsync<AuthSessionResponse>(Json))!;
        var account = new Account(session.User.Id, email, phone, session.Tokens.AccessToken);

        using var authed = host.CreateClient();
        Authorize(authed, account.AccessToken);
        var code = host.Codes.CodeFor(OtpPurpose.PhoneVerification, phone);
        (await authed.PostAsJsonAsync(
            "/v1/auth/otp/verify", new OtpVerifyRequest(phone, code), Json))
            .EnsureSuccessStatusCode();

        return account;
    }

    private static async Task<Account> FundedUserAsync(IslaPayHost host, string amount)
    {
        var account = await VerifiedUserAsync(host);
        var money = Money.Parse(amount, TestCurrencies.EIsla);

        await host.Services.GetRequiredService<ILedger>().PostAsync(new PostingRequest(
            Kind: "settlement",
            Legs:
            [
                new PostingLeg(AccountRef.User(account.UserId, TestCurrencies.EIsla), money),
                new PostingLeg(AccountRef.CashFloat(TestCurrencies.EIsla), -money),
            ]));

        return account;
    }

    private static void Authorize(HttpClient client, string accessToken) =>
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", accessToken);

    private static async Task<Money> EIslaBalanceAsync(ILedger ledger, string userId)
    {
        var balances = await ledger.BalancesAsync(userId);
        return balances.FirstOrDefault(b => b.Currency == TestCurrencies.EIsla)?.Balance
            ?? Money.Zero(TestCurrencies.EIsla);
    }

    private static async Task<T> Read<T>(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync();
        return JsonSerializer.Deserialize<T>(body, Json)
            ?? throw new InvalidOperationException($"Could not read a {typeof(T).Name} from: {body}");
    }
}
