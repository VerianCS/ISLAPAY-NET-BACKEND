using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using IslaPay.Identity;
using IslaPay.Identity.Contracts;
using IslaPay.Ledger.Contracts;
using IslaPay.Marketplace.Contracts;
using IslaPay.Platform;
using IslaPay.Platform.Api;
using IslaPay.Platform.AspNet.Security;
using IslaPay.Platform.Serialization;
using IslaPay.TestSupport;
using IslaPay.Treasury;
using IslaPay.Treasury.Contracts;
using Microsoft.Extensions.DependencyInjection;

namespace IslaPay.EndToEnd.Tests;

/// <summary>
/// The admin console's half of the system, through the running host.
/// </summary>
/// <remarks>
/// <para>
/// The module's own tests prove the arithmetic against a real book. These
/// prove the parts a service test cannot see: that the role policy actually
/// keeps a customer's token out, that the idempotency middleware is wired to
/// the credit, and that <c>Money</c> reaches the wire in a shape a console in
/// another repository can read. Each of those has exactly one chance to be
/// wrong and no unit test that would notice.
/// </para>
/// <para>
/// The escrow reconciliation is exercised against a real marketplace hold
/// rather than a stated one, because the interesting question is not whether
/// the band arithmetic works — that is tested elsewhere — but whether the
/// reporter registered by a module and the balance written by the ledger are
/// talking about the same money.
/// </para>
/// </remarks>
[Collection(IslaPayHostDefinition.Name)]
[Trait("Category", "Integration")]
public class TreasuryTests
{
    private const string Password = "Correct-Horse-9";
    private static readonly JsonSerializerOptions Json = IslaPayJson.Create(TestCurrencies.Scales);

    private readonly IslaPayHostFixture _fixture;

    public TreasuryTests(IslaPayHostFixture fixture) => _fixture = fixture;

    [SkippableFact]
    public async Task A_customers_token_cannot_read_the_platforms_money()
    {
        Skip.IfNot(_fixture.Available, "No Keycloak or no Postgres reachable.");

        await using var host = _fixture.Build();
        var user = await VerifiedUserAsync(host);

        using var client = host.CreateClient();
        Authorize(client, user.AccessToken);

        // Not 404 and not an empty list: a signed-in customer asking where the
        // company's money is gets told no. The role is the whole control, and
        // a test that only checked the happy path would never notice it being
        // dropped from the group.
        var refused = await client.GetAsync(
            new Uri("/v1/admin/treasury/balances", UriKind.Relative));

        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
    }

    [SkippableFact]
    public async Task A_credit_shows_up_on_both_sides_of_the_balances()
    {
        Skip.IfNot(_fixture.Available, "No Keycloak or no Postgres reachable.");

        await using var host = _fixture.Build();
        var admin = await TreasuryAdminAsync(host);
        var mirror = $"bank:{Guid.NewGuid():N}"[..20];

        using var client = host.CreateClient();
        Authorize(client, admin.AccessToken);

        // The difference, not the figure. Every other test in this collection
        // funds its users out of the same float, so an absolute floor would be
        // asserting about whatever ran before.
        var before = await FloatAsync(client);

        var receipt = await Post<CreditReceiptDto>(client, new CreditRequest(
            "float", Money.Parse("1500.00", TestCurrencies.EIsla), mirror, "Fondeo inicial"));

        Assert.True(receipt.Applied);

        var balances = await Read<TreasuryBalancesDto>(await client.GetAsync(
            new Uri("/v1/admin/treasury/balances", UriKind.Relative)));

        var float_ = Assert.Single(
            balances.Accounts, a => a.Owner == "float" && a.Balance.Currency.Code == "EISLA");
        var source = Assert.Single(balances.Accounts, a => a.Mirror == mirror);

        // The two halves of one posting. Their sum is what makes the credit an
        // accounting fact rather than a number somebody typed.
        Assert.Equal(before + 150000, float_.Balance.MinorUnits);
        Assert.Equal(-150000, source.Balance.MinorUnits);
    }

    [SkippableFact]
    public async Task The_same_credit_sent_twice_is_answered_twice_and_applied_once()
    {
        Skip.IfNot(_fixture.Available, "No Keycloak or no Postgres reachable.");

        await using var host = _fixture.Build();
        var admin = await TreasuryAdminAsync(host);

        using var client = host.CreateClient();
        Authorize(client, admin.AccessToken);

        var key = Guid.NewGuid().ToString("N");
        var request = new CreditRequest(
            "settlement_fund", Money.Parse("60.00", TestCurrencies.EIsla),
            "capital", "Un solo ingreso");

        var first = await Post<CreditReceiptDto>(client, request, key);
        // The console's connection dropped and somebody pressed it again. The
        // platform's middleware replays the stored response without the
        // endpoint running at all, which is why this is worth testing from out
        // here: the module never sees the second call.
        var second = await Post<CreditReceiptDto>(client, request, key);

        Assert.Equal(first.PostingId, second.PostingId);
    }

    [SkippableFact]
    public async Task A_live_marketplace_hold_is_what_escrow_is_reconciled_against()
    {
        Skip.IfNot(_fixture.Available, "No Keycloak or no Postgres reachable.");

        await using var host = _fixture.Build();
        var admin = await TreasuryAdminAsync(host);

        using var client = host.CreateClient();
        Authorize(client, admin.AccessToken);

        var before = await Read<TreasuryReconciliationDto>(await client.GetAsync(
            new Uri("/v1/admin/treasury/reconciliation", UriKind.Relative)));

        // A real order, placed by a real buyer against a real listing: the hold
        // is posted by the marketplace and reported by the marketplace, and
        // nothing in this test tells the treasury either figure.
        await PlaceAnOrderAsync(host);

        var after = await Read<TreasuryReconciliationDto>(await client.GetAsync(
            new Uri("/v1/admin/treasury/reconciliation", UriKind.Relative)));

        Assert.True(before.Balanced, Describe(before));
        Assert.True(after.Balanced, Describe(after));

        var row = Assert.Single(after.Currencies, c => c.Currency == "EISLA");
        Assert.Contains(row.Claims, c => c.Context == "marketplace" && c.Held.IsPositive);
    }

    [SkippableFact]
    public async Task One_account_history_is_readable_and_says_who_funded_it()
    {
        Skip.IfNot(_fixture.Available, "No Keycloak or no Postgres reachable.");

        await using var host = _fixture.Build();
        var admin = await TreasuryAdminAsync(host);

        using var client = host.CreateClient();
        Authorize(client, admin.AccessToken);

        await Post<CreditReceiptDto>(client, new CreditRequest(
            "float", Money.Parse("3.00", TestCurrencies.EIsla), "capital", "Para la historia"));

        var page = await Read<LedgerEntryPage>(await client.GetAsync(
            new Uri("/v1/admin/treasury/accounts/float/EISLA/entries?limit=5", UriKind.Relative)));

        var entry = page.Items[0];
        Assert.Equal("funding", entry.Kind);
        Assert.Equal(admin.UserId, entry.Metadata["by"]);
        Assert.Equal("Para la historia", entry.Metadata["reason"]);
    }

    // ---------------------------------------------------------------- helpers

    /// <summary>What the float holds in E-ISLA right now, in minor units.</summary>
    private static async Task<long> FloatAsync(HttpClient client)
    {
        var balances = await Read<TreasuryBalancesDto>(await client.GetAsync(
            new Uri("/v1/admin/treasury/balances", UriKind.Relative)));

        return balances.Accounts
            .Where(a => a.Owner == "float" && a.Balance.Currency.Code == "EISLA")
            .Sum(a => a.Balance.MinorUnits);
    }

    private static string Describe(TreasuryReconciliationDto report) =>
        string.Join("; ", report.Currencies
            .Where(c => !c.Balanced)
            .Select(c => $"{c.Currency}: ledger {c.Ledger}, claimed {c.Claimed}, off by {c.Difference}"));

    private sealed record Account(string UserId, string Email, string Phone, string AccessToken);

    private async Task<Account> TreasuryAdminAsync(IslaPayHost host)
    {
        var account = await VerifiedUserAsync(host);
        await _fixture.Keycloak.GrantRealmRoleAsync(account.UserId, StaffRoles.TreasuryOperator);

        // Signed in again: roles are baked into a token when it is issued, and
        // the one from registration predates the grant.
        using var client = host.CreateClient();
        var response = await client.PostAsJsonAsync(
            "/v1/auth/login", new LoginRequest(account.Email, Password), Json);
        response.EnsureSuccessStatusCode();

        var session = (await response.Content.ReadFromJsonAsync<AuthSessionResponse>(Json))!;
        return account with { AccessToken = session.Tokens.AccessToken };
    }

    /// <summary>A listing bought with a real hold, so escrow has a reason.</summary>
    private static async Task PlaceAnOrderAsync(IslaPayHost host)
    {
        var seller = await VerifiedUserAsync(host);
        var buyer = await VerifiedUserAsync(host);

        using var scope = host.Services.CreateScope();
        var ledger = scope.ServiceProvider.GetRequiredService<ILedger>();
        await ledger.PostAsync(new PostingRequest(
            Kind: "settlement",
            Legs:
            [
                new PostingLeg(
                    AccountRef.User(buyer.UserId, TestCurrencies.EIsla),
                    Money.Parse("500.00", TestCurrencies.EIsla)),
                new PostingLeg(
                    AccountRef.CashFloat(TestCurrencies.EIsla),
                    Money.Parse("-500.00", TestCurrencies.EIsla)),
            ],
            IdempotencyKey: $"e2e:treasury:{Guid.NewGuid():N}"));

        using var sellerClient = host.CreateClient();
        Authorize(sellerClient, seller.AccessToken);
        var listing = await Read<ListingDto>(await sellerClient.PostAsJsonAsync(
            "/v1/listings",
            new PublishListingRequest(
                Title: "Bicicleta",
                Description: "Para la conciliación",
                Category: "Deportes",
                Condition: "Como nuevo",
                Price: Money.Parse("120.00", TestCurrencies.EIsla),
                Location: "Habana"),
            Json));

        using var buyerClient = host.CreateClient();
        Authorize(buyerClient, buyer.AccessToken);
        buyerClient.DefaultRequestHeaders.Add("Idempotency-Key", Guid.NewGuid().ToString("N"));
        var order = await buyerClient.PostAsJsonAsync(
            "/v1/orders", new PlaceOrderRequest(Guid.Parse(listing.Id)), Json);

        Assert.True(order.IsSuccessStatusCode, await order.Content.ReadAsStringAsync());
    }

    private static async Task<Account> VerifiedUserAsync(IslaPayHost host)
    {
        using var client = host.CreateClient();

        var id = Guid.NewGuid().ToString("N")[..10];
        var email = $"tesoro-{id}@islapay.cu";
        var phone = "+53" + Random.Shared.NextInt64(500_000_000, 599_999_999)
            .ToString(System.Globalization.CultureInfo.InvariantCulture);

        var registered = await client.PostAsJsonAsync(
            "/v1/auth/register", new RegisterRequest("Ana Pérez", email, phone, Password), Json);
        var session = await Read<AuthSessionResponse>(registered);
        var account = new Account(session.User.Id, email, phone, session.Tokens.AccessToken);

        Authorize(client, account.AccessToken);
        var code = host.Codes.CodeFor(OtpPurpose.PhoneVerification, phone);
        var done = await client.PostAsJsonAsync(
            "/v1/auth/otp/verify", new OtpVerifyRequest(phone, code), Json);
        Assert.True(done.IsSuccessStatusCode, await done.Content.ReadAsStringAsync());

        return account;
    }

    private static async Task<T> Post<T>(
        HttpClient client, CreditRequest request, string? key = null)
    {
        using var message = new HttpRequestMessage(
            HttpMethod.Post, new Uri("/v1/admin/treasury/credits", UriKind.Relative))
        {
            Content = JsonContent.Create(request, options: Json),
        };
        message.Headers.Add("Idempotency-Key", key ?? Guid.NewGuid().ToString("N"));

        return await Read<T>(await client.SendAsync(message));
    }

    private static void Authorize(HttpClient client, string token) =>
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

    private static async Task<T> Read<T>(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, $"{(int)response.StatusCode}: {body}");
        return JsonSerializer.Deserialize<T>(body, Json)!;
    }
}
