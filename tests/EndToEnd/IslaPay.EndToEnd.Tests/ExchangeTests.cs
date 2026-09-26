using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using IslaPay.Exchange.Contracts;
using IslaPay.Identity;
using IslaPay.Identity.Contracts;
using IslaPay.Ledger.Contracts;
using IslaPay.Platform;
using IslaPay.Platform.Api;
using IslaPay.Platform.AspNet;
using IslaPay.Platform.Serialization;
using IslaPay.TestSupport;
using IslaPay.Wallet.Contracts;
using Microsoft.Extensions.DependencyInjection;

namespace IslaPay.EndToEnd.Tests;

/// <summary>
/// Converting between what a customer holds, through the host.
/// </summary>
/// <remarks>
/// Every figure is a difference: the settlement fund and the fees are shared
/// with every other test in the collection.
/// </remarks>
[Collection(IslaPayHostDefinition.Name)]
[Trait("Category", "Integration")]
public class ExchangeTests
{
    private const string Password = "Correct-Horse-9";
    private static readonly JsonSerializerOptions Json = IslaPayJson.Create(TestCurrencies.Scales);

    private readonly IslaPayHostFixture _fixture;

    public ExchangeTests(IslaPayHostFixture fixture) => _fixture = fixture;

    [SkippableFact]
    public async Task Usdt_becomes_e_isla_at_par_less_one_per_cent_and_the_usdt_goes_to_the_reserve()
    {
        Skip.IfNot(_fixture.Available, "No Keycloak or no Postgres reachable.");

        await using var host = _fixture.Build();
        var customer = await CustomerAsync(host);
        await PostAsync(host, "fund", AccountRef.User(customer.UserId, TestCurrencies.Usdt),
            AccountRef.CashFloat(TestCurrencies.Usdt), Money.Parse("100", TestCurrencies.Usdt));
        await StockAsync(host, "500.00");

        var fundUsdt = await BalanceAsync(host, AccountRef.SettlementFund(TestCurrencies.Usdt));
        var fees = await BalanceAsync(host, AccountRef.Fees(TestCurrencies.Usdt));

        using var client = Client(host, customer);
        var quote = await Read<QuoteResponse>(await client.PostAsJsonAsync(
            "/v1/exchange/quotes", new QuoteRequest(Money.Parse("100", TestCurrencies.Usdt), "EISLA"), Json));

        Assert.True(quote.Executable, quote.Reason);
        Assert.Equal("1.0000", quote.Rate);
        Assert.Equal(Money.Parse("1", TestCurrencies.Usdt), quote.Fee);
        Assert.Equal(Money.Parse("99.00", TestCurrencies.EIsla), quote.Received);

        var receipt = await Read<ConversionReceipt>(await ConvertAsync(client, quote.QuoteId));
        Assert.True(receipt.Applied);

        var wallet = await Read<WalletResponse>(await client.GetAsync(new Uri("/v1/me/wallet", UriKind.Relative)));
        Assert.Contains(wallet.Accounts, a => a.Balance.Currency.Code == "USDT" && a.Balance.MinorUnits == 0);
        Assert.Contains(wallet.Accounts, a => a.Balance.Currency.Code == "EISLA" && a.Balance.MinorUnits == 9900);
        Assert.Equal("1.0000", wallet.Rates["USDT_EISLA"]);

        // The USDT is the reserve now; the fee is revenue.
        Assert.Equal(fundUsdt + 99_000_000, await BalanceAsync(host, AccountRef.SettlementFund(TestCurrencies.Usdt)));
        Assert.Equal(fees + 1_000_000, await BalanceAsync(host, AccountRef.Fees(TestCurrencies.Usdt)));
    }

    [SkippableFact]
    public async Task One_quote_converts_once_however_often_it_is_sent()
    {
        Skip.IfNot(_fixture.Available, "No Keycloak or no Postgres reachable.");

        await using var host = _fixture.Build();
        var customer = await CustomerAsync(host);
        await PostAsync(host, "fund", AccountRef.User(customer.UserId, TestCurrencies.Usdc),
            AccountRef.CashFloat(TestCurrencies.Usdc), Money.Parse("50", TestCurrencies.Usdc));
        await StockAsync(host, "500.00");

        using var client = Client(host, customer);
        var quote = await Read<QuoteResponse>(await client.PostAsJsonAsync(
            "/v1/exchange/quotes", new QuoteRequest(Money.Parse("20", TestCurrencies.Usdc), "EISLA"), Json));

        var first = await Read<ConversionReceipt>(await ConvertAsync(client, quote.QuoteId));
        // A new key: the middleware does not know it is a retry, the ledger does.
        var second = await Read<ConversionReceipt>(await ConvertAsync(client, quote.QuoteId));

        Assert.True(first.Applied);
        Assert.False(second.Applied);
        Assert.Equal(first.PostingId, second.PostingId);
        Assert.Equal(Money.Parse("30", TestCurrencies.Usdc).MinorUnits,
            await BalanceAsync(host, AccountRef.User(customer.UserId, TestCurrencies.Usdc)));
    }

    [SkippableFact]
    public async Task A_quote_says_why_it_cannot_run_and_a_forged_one_is_refused()
    {
        Skip.IfNot(_fixture.Available, "No Keycloak or no Postgres reachable.");

        await using var host = _fixture.Build();
        var customer = await CustomerAsync(host);
        var stranger = await CustomerAsync(host);
        using var client = Client(host, customer);

        // Nothing to convert: a successful answer that says why not.
        var broke = await Read<QuoteResponse>(await client.PostAsJsonAsync(
            "/v1/exchange/quotes", new QuoteRequest(Money.Parse("10", TestCurrencies.Usdt), "EISLA"), Json));
        Assert.False(broke.Executable);
        Assert.Equal("insufficient_funds", broke.Reason);

        // More than the fund could pay out.
        var fund = await BalanceAsync(host, AccountRef.SettlementFund(TestCurrencies.Usdc));
        var much = Money.FromMinorUnits(Math.Max(fund, 0) + 1_000_000_000, TestCurrencies.Usdc);
        await PostAsync(host, "fund", AccountRef.User(customer.UserId, TestCurrencies.EIsla),
            AccountRef.Issuer(TestCurrencies.EIsla), Money.FromMinorUnits(much.MinorUnits / 10_000 * 2, TestCurrencies.EIsla));
        var dry = await Read<QuoteResponse>(await client.PostAsJsonAsync(
            "/v1/exchange/quotes",
            new QuoteRequest(Money.FromMinorUnits(much.MinorUnits / 10_000 * 2, TestCurrencies.EIsla), "USDC"), Json));
        Assert.False(dry.Executable);
        Assert.Equal(ExchangeErrors.FundUnavailable, dry.Reason);

        // Somebody else's quote, and one made up.
        using var other = Client(host, stranger);
        var theirs = await ConvertAsync(other, dry.QuoteId);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, theirs.StatusCode);
        Assert.Equal(ExchangeErrors.QuoteInvalid, (await Problem(theirs)).Code);

        var made = await ConvertAsync(client, "not-a-quote");
        Assert.Equal(ExchangeErrors.QuoteInvalid, (await Problem(made)).Code);

        // A pair that does not exist.
        var cup = await client.PostAsJsonAsync(
            "/v1/exchange/quotes", new QuoteRequest(Money.Parse("10", TestCurrencies.Usdt), "CUP"), Json);
        Assert.Equal(ExchangeErrors.PairUnavailable, (await Problem(cup)).Code);
    }

    // ---------------------------------------------------------------- helpers

    private sealed record Account(string UserId, string Email, string AccessToken);

    private static async Task<Account> CustomerAsync(IslaPayHost host)
    {
        using var client = host.CreateClient();
        var email = $"fx-{Guid.NewGuid():N}"[..13] + "@islapay.cu";
        var phone = "+53" + Random.Shared.NextInt64(500_000_000, 599_999_999)
            .ToString(System.Globalization.CultureInfo.InvariantCulture);
        var session = await Read<AuthSessionResponse>(await client.PostAsJsonAsync(
            "/v1/auth/register", new RegisterRequest("Ana Pérez", email, phone, Password), Json));

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", session.Tokens.AccessToken);
        var code = host.Codes.CodeFor(OtpPurpose.PhoneVerification, phone);
        (await client.PostAsJsonAsync("/v1/auth/otp/verify", new OtpVerifyRequest(phone, code), Json))
            .EnsureSuccessStatusCode();
        return new Account(session.User.Id, email, session.Tokens.AccessToken);
    }

    /// <summary>E-ISLA in the settlement fund, straight from the issuer.</summary>
    private static Task StockAsync(IslaPayHost host, string amount) =>
        PostAsync(host, "stock", AccountRef.SettlementFund(TestCurrencies.EIsla),
            AccountRef.Issuer(TestCurrencies.EIsla), Money.Parse(amount, TestCurrencies.EIsla));

    private static async Task PostAsync(IslaPayHost host, string why, AccountRef to, AccountRef from, Money amount)
    {
        using var scope = host.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<ILedger>().PostAsync(new PostingRequest(
            Kind: from.Owner == AccountOwner.Issuer ? "issuance" : "settlement",
            Legs: [new PostingLeg(to, amount), new PostingLeg(from, -amount)],
            IdempotencyKey: $"e2e:fx:{why}:{Guid.NewGuid():N}"));
    }

    private static async Task<long> BalanceAsync(IslaPayHost host, AccountRef account)
    {
        using var scope = host.Services.CreateScope();
        return (await scope.ServiceProvider.GetRequiredService<ILedger>().BalanceOfAsync(account)).MinorUnits;
    }

    private static async Task<HttpResponseMessage> ConvertAsync(HttpClient client, string quoteId)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/v1/exchange/conversions")
        {
            Content = JsonContent.Create(new ConversionRequest(quoteId), options: Json),
        };
        request.Headers.Add(IdempotencyMiddleware.HeaderName, Guid.NewGuid().ToString("N"));
        return await client.SendAsync(request);
    }

    private static HttpClient Client(IslaPayHost host, Account account)
    {
        var client = host.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", account.AccessToken);
        return client;
    }

    private static async Task<ApiProblem> Problem(HttpResponseMessage response) =>
        JsonSerializer.Deserialize<ApiProblem>(await response.Content.ReadAsStringAsync(), Json)!;

    private static async Task<T> Read<T>(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, $"{(int)response.StatusCode}: {body}");
        return JsonSerializer.Deserialize<T>(body, Json)!;
    }
}
