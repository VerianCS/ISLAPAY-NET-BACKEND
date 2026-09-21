using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using IslaPay.Identity;
using IslaPay.Identity.Contracts;
using IslaPay.Ledger.Contracts;
using IslaPay.Marketplace;
using IslaPay.Marketplace.Contracts;
using IslaPay.Platform;
using IslaPay.Platform.Api;
using IslaPay.Platform.AspNet;
using IslaPay.Platform.Serialization;
using Microsoft.Extensions.DependencyInjection;

namespace IslaPay.EndToEnd.Tests;

/// <summary>
/// Selling something to somebody, through the API a phone would use.
/// </summary>
/// <remarks>
/// The module's own tests check the state machine against a fake ledger,
/// because a fake can be made to die at the interesting moment. These check
/// the thing a fake cannot: that the double-entry records really balance, that
/// escrow really empties, and that the buyer's and the seller's wallets really
/// show what happened.
/// </remarks>
[Collection(IslaPayHostDefinition.Name)]
[Trait("Category", "Integration")]
public class MarketplaceTests
{
    private const string Password = "Correct-Horse-9";
    private static readonly JsonSerializerOptions Json = IslaPayJson.Options;

    private readonly IslaPayHostFixture _fixture;

    public MarketplaceTests(IslaPayHostFixture fixture) => _fixture = fixture;

    [SkippableFact]
    public async Task A_sale_moves_the_price_from_the_buyer_to_the_seller_less_the_commission()
    {
        Skip.IfNot(_fixture.Available, "No Keycloak or no Postgres reachable.");

        await using var host = _fixture.Build();
        var seller = await VerifiedUserAsync(host);
        var buyer = await FundedUserAsync(host, "60.00");

        var listing = await PublishAsync(host, seller, "50.00");
        var order = await Read<OrderDto>(await PlaceOrderAsync(host, buyer, listing.Id));

        var ledger = host.Services.GetRequiredService<ILedger>();

        // While the code is unscanned the money is genuinely out of the
        // buyer's account. A reservation that left the balance alone would let
        // the same 60 be promised to three sellers.
        Assert.Equal("10.00", (await EIslaBalanceAsync(ledger, buyer.UserId)).ToString());
        Assert.Equal("0.00", (await EIslaBalanceAsync(ledger, seller.UserId)).ToString());

        var released = await Read<OrderDto>(await RedeemAsync(host, seller, order.Code!));

        Assert.Equal(OrderStatuses.Released, released.Status);
        Assert.Equal("10.00", (await EIslaBalanceAsync(ledger, buyer.UserId)).ToString());
        Assert.Equal("49.50", (await EIslaBalanceAsync(ledger, seller.UserId)).ToString());
    }

    [SkippableFact]
    public async Task The_money_waiting_to_be_collected_is_in_escrow_and_escrow_empties()
    {
        Skip.IfNot(_fixture.Available, "No Keycloak or no Postgres reachable.");

        await using var host = _fixture.Build();
        var seller = await VerifiedUserAsync(host);
        var buyer = await FundedUserAsync(host, "30.00");

        var before = await EscrowAsync(host);
        var listing = await PublishAsync(host, seller, "20.00");
        var order = await Read<OrderDto>(await PlaceOrderAsync(host, buyer, listing.Id));

        Assert.Equal(2000, (await EscrowAsync(host)).MinorUnits - before.MinorUnits);

        await RedeemAsync(host, seller, order.Code!);

        // Back to where it started. Escrow is a platform account and may go
        // negative, so a payout that happened twice would not bounce — it
        // would leave this figure below zero and nothing would have said so.
        Assert.Equal(before, await EscrowAsync(host));
    }

    [SkippableFact]
    public async Task A_buyer_who_cannot_afford_it_is_told_so_and_the_item_stays_on_offer()
    {
        Skip.IfNot(_fixture.Available, "No Keycloak or no Postgres reachable.");

        await using var host = _fixture.Build();
        var seller = await VerifiedUserAsync(host);
        var buyer = await FundedUserAsync(host, "5.00");

        var listing = await PublishAsync(host, seller, "50.00");
        var response = await PlaceOrderAsync(host, buyer, listing.Id);

        Assert.Equal(HttpStatusCode.UnprocessableContent, response.StatusCode);
        var problem = await Read<ApiProblem>(response);

        Assert.Equal(MarketplaceErrors.InsufficientFunds, problem.Code);
        Assert.NotNull(problem.Meta);
        Assert.Equal("EISLA", problem.Meta!["currency"].ToString());

        var again = await Read<ListingDto>(await GetAsync(host, buyer, $"/v1/listings/{listing.Id}"));
        Assert.Equal(ListingStatuses.Active, again.Status);
    }

    [SkippableFact]
    public async Task A_retry_with_the_same_key_locks_one_hold()
    {
        Skip.IfNot(_fixture.Available, "No Keycloak or no Postgres reachable.");

        await using var host = _fixture.Build();
        var seller = await VerifiedUserAsync(host);
        var buyer = await FundedUserAsync(host, "40.00");
        var listing = await PublishAsync(host, seller, "25.00");

        var key = Guid.NewGuid().ToString("N");
        var first = await Read<OrderDto>(await PlaceOrderAsync(host, buyer, listing.Id, key));
        var second = await Read<OrderDto>(await PlaceOrderAsync(host, buyer, listing.Id, key));

        // The same answer, not a second hold — which would have taken another
        // 25 out of a balance that only has 15 left.
        Assert.Equal(first.Id, second.Id);
        Assert.Equal(first.Code, second.Code);

        var ledger = host.Services.GetRequiredService<ILedger>();
        Assert.Equal("15.00", (await EIslaBalanceAsync(ledger, buyer.UserId)).ToString());
    }

    [SkippableFact]
    public async Task Cancelling_puts_the_money_back_and_the_item_back_on_offer()
    {
        Skip.IfNot(_fixture.Available, "No Keycloak or no Postgres reachable.");

        await using var host = _fixture.Build();
        var seller = await VerifiedUserAsync(host);
        var buyer = await FundedUserAsync(host, "40.00");
        var listing = await PublishAsync(host, seller, "30.00");
        var order = await Read<OrderDto>(await PlaceOrderAsync(host, buyer, listing.Id));

        var cancelled = await Read<OrderDto>(await CancelAsync(host, buyer, order.Id));

        Assert.Equal(OrderStatuses.Cancelled, cancelled.Status);

        var ledger = host.Services.GetRequiredService<ILedger>();
        Assert.Equal("40.00", (await EIslaBalanceAsync(ledger, buyer.UserId)).ToString());

        var again = await Read<ListingDto>(await GetAsync(host, buyer, $"/v1/listings/{listing.Id}"));
        Assert.Equal(ListingStatuses.Active, again.Status);
    }

    [SkippableFact]
    public async Task The_seller_cannot_read_the_code_that_pays_them()
    {
        Skip.IfNot(_fixture.Available, "No Keycloak or no Postgres reachable.");

        await using var host = _fixture.Build();
        var seller = await VerifiedUserAsync(host);
        var buyer = await FundedUserAsync(host, "20.00");
        var listing = await PublishAsync(host, seller, "10.00");
        var order = await Read<OrderDto>(await PlaceOrderAsync(host, buyer, listing.Id));

        var asSeller = await Read<OrderDto>(await GetAsync(host, seller, $"/v1/orders/{order.Id}"));
        var asBuyer = await Read<OrderDto>(await GetAsync(host, buyer, $"/v1/orders/{order.Id}"));

        Assert.NotNull(asBuyer.Code);
        Assert.Null(asSeller.Code);
    }

    [SkippableFact]
    public async Task A_stranger_can_neither_read_the_order_nor_spend_the_code()
    {
        Skip.IfNot(_fixture.Available, "No Keycloak or no Postgres reachable.");

        await using var host = _fixture.Build();
        var seller = await VerifiedUserAsync(host);
        var buyer = await FundedUserAsync(host, "20.00");
        var stranger = await VerifiedUserAsync(host);

        var listing = await PublishAsync(host, seller, "10.00");
        var order = await Read<OrderDto>(await PlaceOrderAsync(host, buyer, listing.Id));

        var peek = await GetAsync(host, stranger, $"/v1/orders/{order.Id}");
        Assert.Equal(HttpStatusCode.NotFound, peek.StatusCode);

        var stolen = await RedeemAsync(host, stranger, order.Code!);
        Assert.Equal(HttpStatusCode.UnprocessableContent, stolen.StatusCode);
        Assert.Equal(MarketplaceErrors.CodeInvalid, (await Read<ApiProblem>(stolen)).Code);

        // And no money moved anywhere.
        var ledger = host.Services.GetRequiredService<ILedger>();
        Assert.Equal("0.00", (await EIslaBalanceAsync(ledger, stranger.UserId)).ToString());
        Assert.Equal("0.00", (await EIslaBalanceAsync(ledger, seller.UserId)).ToString());
    }

    [SkippableFact]
    public async Task An_unverified_phone_can_list_but_not_buy()
    {
        Skip.IfNot(_fixture.Available, "No Keycloak or no Postgres reachable.");

        await using var host = _fixture.Build();
        var seller = await VerifiedUserAsync(host);
        var unverified = await RegisterAsync(host);

        // Publishing is not a money movement (D11).
        var theirs = await PublishAsync(host, unverified, "5.00");
        Assert.Equal(ListingStatuses.Active, theirs.Status);

        var listing = await PublishAsync(host, seller, "5.00");
        var response = await PlaceOrderAsync(host, unverified, listing.Id);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(MarketplaceErrors.PhoneNotVerified, (await Read<ApiProblem>(response)).Code);
    }

    [SkippableFact]
    public async Task The_sale_shows_up_in_both_wallets()
    {
        Skip.IfNot(_fixture.Available, "No Keycloak or no Postgres reachable.");

        await using var host = _fixture.Build();
        var seller = await VerifiedUserAsync(host);
        var buyer = await FundedUserAsync(host, "40.00");
        var listing = await PublishAsync(host, seller, "20.00");
        var order = await Read<OrderDto>(await PlaceOrderAsync(host, buyer, listing.Id));
        await RedeemAsync(host, seller, order.Code!);

        var sellerWallet = await Read<Wallet.Contracts.WalletResponse>(
            await GetAsync(host, seller, "/v1/me/wallet"));
        var buyerWallet = await Read<Wallet.Contracts.WalletResponse>(
            await GetAsync(host, buyer, "/v1/me/wallet"));

        // The seller sees one credit of 19.80 — the price less 1% — and the
        // title of what they sold, which came along on the posting's metadata.
        var credit = Assert.Single(sellerWallet.Transactions.Items);
        Assert.Equal("19.80", credit.Amount.ToString());
        Assert.Equal("Bicicleta", credit.Meta["listingTitle"]);

        // The buyer sees the hold going out and nothing coming back.
        Assert.Contains(buyerWallet.Transactions.Items, t => t.Amount.ToString() == "-20.00");
        Assert.Equal("20.00", (await EIslaBalanceAsync(
            host.Services.GetRequiredService<ILedger>(), buyer.UserId)).ToString());
    }

    [SkippableFact]
    public async Task Only_active_listings_are_browsable_and_a_reserved_one_drops_out()
    {
        Skip.IfNot(_fixture.Available, "No Keycloak or no Postgres reachable.");

        await using var host = _fixture.Build();
        var seller = await VerifiedUserAsync(host);
        var buyer = await FundedUserAsync(host, "40.00");
        var listing = await PublishAsync(host, seller, "12.00");

        var onOffer = await Read<CursorPage<ListingDto>>(
            await GetAsync(host, buyer, "/v1/listings?limit=100"));
        Assert.Contains(onOffer.Items, l => l.Id == listing.Id);

        await PlaceOrderAsync(host, buyer, listing.Id);

        var after = await Read<CursorPage<ListingDto>>(
            await GetAsync(host, buyer, "/v1/listings?limit=100"));
        Assert.DoesNotContain(after.Items, l => l.Id == listing.Id);
    }

    [SkippableFact]
    public async Task An_expired_hold_is_returned_by_the_sweeper()
    {
        Skip.IfNot(_fixture.Available, "No Keycloak or no Postgres reachable.");

        await using var host = _fixture.Build();
        var seller = await VerifiedUserAsync(host);
        var buyer = await FundedUserAsync(host, "40.00");
        var listing = await PublishAsync(host, seller, "18.00");
        var order = await Read<OrderDto>(await PlaceOrderAsync(host, buyer, listing.Id));

        var ledger = host.Services.GetRequiredService<ILedger>();
        Assert.Equal("22.00", (await EIslaBalanceAsync(ledger, buyer.UserId)).ToString());

        // Rather than waiting 72 hours: move the deadline into the past and
        // run the same repair the hosted sweeper runs on its timer.
        await ExpireAsync(host, Guid.Parse(order.Id));
        using var scope = host.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<MarketplaceService>().RepairAsync();

        Assert.Equal("40.00", (await EIslaBalanceAsync(ledger, buyer.UserId)).ToString());

        var settled = await Read<OrderDto>(await GetAsync(host, buyer, $"/v1/orders/{order.Id}"));
        Assert.Equal(OrderStatuses.Expired, settled.Status);

        var again = await Read<ListingDto>(await GetAsync(host, buyer, $"/v1/listings/{listing.Id}"));
        Assert.Equal(ListingStatuses.Active, again.Status);
    }

    // ---------------------------------------------------------------- helpers

    private sealed record Account(string UserId, string Email, string Phone, string AccessToken);

    private static async Task<ListingDto> PublishAsync(
        IslaPayHost host, Account seller, string price)
    {
        using var client = host.CreateClient();
        Authorize(client, seller.AccessToken);

        var response = await client.PostAsJsonAsync(
            "/v1/listings",
            new PublishListingRequest(
                Title: "Bicicleta",
                Description: "Poco uso",
                Category: "Deportes",
                Condition: "Como nuevo",
                Price: Money.Parse(price, Currency.EIsla),
                Location: "Habana"),
            Json);

        Assert.True(response.IsSuccessStatusCode,
            $"publish returned {(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");

        return await Read<ListingDto>(response);
    }

    private static Task<HttpResponseMessage> PlaceOrderAsync(
        IslaPayHost host, Account buyer, string listingId, string? key = null) =>
        PostAsync(host, buyer, "/v1/orders", new PlaceOrderRequest(Guid.Parse(listingId)), key);

    private static Task<HttpResponseMessage> RedeemAsync(
        IslaPayHost host, Account seller, string code) =>
        PostAsync(host, seller, "/v1/orders/redeem", new RedeemOrderRequest(code));

    private static Task<HttpResponseMessage> CancelAsync(
        IslaPayHost host, Account actor, string orderId) =>
        PostAsync(host, actor, $"/v1/orders/{orderId}/cancel", new { });

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
        request.Headers.Add(
            IdempotencyMiddleware.HeaderName, key ?? Guid.NewGuid().ToString("N"));

        return await client.SendAsync(request);
    }

    private static async Task<HttpResponseMessage> GetAsync(
        IslaPayHost host, Account actor, string path)
    {
        using var client = host.CreateClient();
        Authorize(client, actor.AccessToken);
        return await client.GetAsync(new Uri(path, UriKind.Relative));
    }

    /// <summary>Moves a hold's deadline into the past.</summary>
    private static async Task ExpireAsync(IslaPayHost host, Guid orderId)
    {
        var database = host.Services.GetRequiredService<Platform.Data.IDatabase>();
        await using var connection = await database.OpenAsync();
        await using var command = new Npgsql.NpgsqlCommand(
            "UPDATE marketplace.orders SET expires_at = now() - interval '1 minute' WHERE id = @id;",
            connection);
        command.Parameters.AddWithValue("id", orderId);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<Money> EscrowAsync(IslaPayHost host)
    {
        var database = host.Services.GetRequiredService<Platform.Data.IDatabase>();
        await using var connection = await database.OpenAsync();
        await using var command = new Npgsql.NpgsqlCommand("""
            SELECT coalesce(sum(e.minor_units), 0)::bigint
            FROM ledger.entries e
            JOIN ledger.accounts a ON a.id = e.account_id
            WHERE a.owner_type = 'platform' AND a.owner = 'escrow' AND e.currency = 'EISLA';
            """, connection);

        return Money.FromMinorUnits((long)(await command.ExecuteScalarAsync())!, Currency.EIsla);
    }

    private static async Task<Account> RegisterAsync(IslaPayHost host)
    {
        using var client = host.CreateClient();
        var email = $"mkt-{Guid.NewGuid():N}@example.test";
        var phone = $"+5355{Random.Shared.Next(100000, 999999)}";

        var response = await client.PostAsJsonAsync(
            "/v1/auth/register",
            new RegisterRequest("Ana Pérez", email, phone, Password), Json);

        Assert.True(response.IsSuccessStatusCode,
            $"register returned {(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");

        var session = await Read<AuthSessionResponse>(response);
        return new Account(session.User.Id, email, phone, session.Tokens.AccessToken);
    }

    private static async Task<Account> VerifiedUserAsync(IslaPayHost host)
    {
        var account = await RegisterAsync(host);

        using var client = host.CreateClient();
        Authorize(client, account.AccessToken);

        var code = host.Codes.CodeFor(OtpPurpose.PhoneVerification, account.Phone);
        var response = await client.PostAsJsonAsync(
            "/v1/auth/otp/verify", new OtpVerifyRequest(account.Phone, code), Json);

        Assert.True(response.IsSuccessStatusCode,
            $"verify returned {(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");

        return account;
    }

    private static async Task<Account> FundedUserAsync(IslaPayHost host, string amount)
    {
        var account = await VerifiedUserAsync(host);
        var money = Money.Parse(amount, Currency.EIsla);

        // There is no deposit endpoint yet, so this posts a settlement against
        // the platform's float — the call a recharge will make when it exists.
        await host.Services.GetRequiredService<ILedger>().PostAsync(new PostingRequest(
            Kind: "settlement",
            Legs:
            [
                new PostingLeg(AccountRef.User(account.UserId, Currency.EIsla), money),
                new PostingLeg(AccountRef.CashFloat(Currency.EIsla), -money),
            ],
            Metadata: new Dictionary<string, string>(StringComparer.Ordinal) { ["method"] = "test" }));

        return account;
    }

    private static void Authorize(HttpClient client, string accessToken) =>
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", accessToken);

    /// <summary>
    /// The account's E-ISLA balance, or zero if it has no E-ISLA account yet.
    /// </summary>
    /// <remarks>
    /// Zero rather than an exception: accounts are opened by the
    /// <c>user.registered</c> event or by the first wallet read, and a seller
    /// in these tests has done neither until somebody pays them. "No account"
    /// and "an empty account" are the same answer to what this asks.
    /// </remarks>
    private static async Task<Money> EIslaBalanceAsync(ILedger ledger, string userId)
    {
        var balances = await ledger.BalancesAsync(userId);
        return balances.FirstOrDefault(b => b.Currency == Currency.EIsla)?.Balance
            ?? Money.Zero(Currency.EIsla);
    }

    private static async Task<T> Read<T>(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync();
        return JsonSerializer.Deserialize<T>(body, Json)
            ?? throw new InvalidOperationException($"Could not read a {typeof(T).Name} from: {body}");
    }
}
