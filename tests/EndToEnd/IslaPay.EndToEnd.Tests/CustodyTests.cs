using IslaPay.TestSupport;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using IslaPay.Catalog.Contracts;
using IslaPay.Custody;
using IslaPay.Custody.Contracts;
using IslaPay.Identity;
using IslaPay.Identity.Contracts;
using IslaPay.Ledger.Contracts;
using IslaPay.Platform;
using IslaPay.Platform.Api;
using IslaPay.Platform.Serialization;
using Microsoft.Extensions.DependencyInjection;

namespace IslaPay.EndToEnd.Tests;

/// <summary>
/// Deposits, through the API a phone would use and the service a scanner
/// would call.
/// </summary>
/// <remarks>
/// The module's own tests prove the rules against a fake ledger. These prove
/// the wiring: that the address a client is handed is the one the scanner
/// attributes money to, and that a credited deposit shows up in the wallet the
/// user already had — with the real ledger, the real posting and the real
/// balance.
/// </remarks>
[Collection(IslaPayHostDefinition.Name)]
[Trait("Category", "Integration")]
public class CustodyTests
{
    private const string Password = "Correct-Horse-9";

    /// <summary>The one asset-and-chain pair this build has switched on.</summary>
    /// <remarks>
    /// A currency <i>and</i> a network, because a chain carries several assets
    /// now. The confirmations are not written here — they are read from the
    /// running host's catalogue, so this asserts that what the API publishes
    /// is what the table says rather than that two constants agree.
    /// </remarks>
    private const string Asset = CurrencyCodes.Usdt;

    private const string Chain = "tron";
    private static readonly JsonSerializerOptions Json = IslaPayJson.Create(TestCurrencies.Scales);

    private readonly IslaPayHostFixture _fixture;

    public CustodyTests(IslaPayHostFixture fixture) => _fixture = fixture;

    [SkippableFact]
    public async Task An_address_is_issued_and_then_the_same_one_comes_back()
    {
        Skip.IfNot(_fixture.Available, "No Keycloak or no Postgres reachable.");

        await using var host = _fixture.Build();
        var user = await VerifiedUserAsync(host);

        var first = await AddressAsync(host, user);
        var again = await AddressAsync(host, user);

        Assert.Equal(first.Address, again.Address);
        Assert.Equal("USDT", first.Currency);
        Assert.Equal(Pair(host).Confirmations, first.Confirmations);
        Assert.StartsWith("T", first.Address, StringComparison.Ordinal);
    }

    [SkippableFact]
    public async Task An_unproved_phone_gets_no_address()
    {
        Skip.IfNot(_fixture.Available, "No Keycloak or no Postgres reachable.");

        await using var host = _fixture.Build();

        // Registered and never verified. The same gate every other way of
        // moving money is behind — a deposit address is what attributes
        // incoming money to a person.
        var user = await RegisterAsync(host);

        using var client = host.CreateClient();
        Authorize(client, user.AccessToken);
        var response = await client.GetAsync(
            new Uri($"/v1/me/custody/addresses/{Asset}/{Chain}", UriKind.Relative));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(
            CustodyErrors.PhoneNotVerified, (await Read<ApiProblem>(response)).Code);
    }

    [SkippableFact]
    public async Task A_deposit_reaching_finality_shows_up_in_the_wallet()
    {
        Skip.IfNot(_fixture.Available, "No Keycloak or no Postgres reachable.");

        await using var host = _fixture.Build();
        var user = await VerifiedUserAsync(host);
        var address = (await AddressAsync(host, user)).Address;

        var seen = new ObservedTransfer(
            Network: Chain,
            Address: address,
            TxHash: "0x" + Guid.NewGuid().ToString("N"),
            Amount: Money.Parse("250.000000", TestCurrencies.Usdt),
            Confirmations: Pair(host).Confirmations);

        // The scanner's entry point. Not an HTTP endpoint, and deliberately:
        // this module's write path belongs to whatever reads the chain.
        await using (var scope = host.Services.CreateAsyncScope())
        {
            var custody = scope.ServiceProvider.GetRequiredService<CustodyService>();
            var credited = await custody.ObserveAsync(seen);
            Assert.Equal(DepositStatuses.Credited, credited!.Status);
        }

        // In the real ledger, in the user's real balance.
        var ledger = host.Services.GetRequiredService<ILedger>();
        var balance = await ledger.BalanceOfAsync(
            AccountRef.User(user.UserId, TestCurrencies.Usdt));
        Assert.Equal("250.000000", balance.ToString());

        // And the mirror account carries the other side, negative — money
        // received from outside.
        var mirror = await ledger.BalanceOfAsync(
            AccountRef.External(Chain, TestCurrencies.Usdt));
        Assert.Equal("-250.000000", mirror.ToString());

        // And the client can see it.
        using var client = host.CreateClient();
        Authorize(client, user.AccessToken);
        var deposits = await Read<List<DepositDto>>(
            await client.GetAsync(new Uri("/v1/me/custody/deposits", UriKind.Relative)));

        var deposit = Assert.Single(deposits);
        Assert.Equal(seen.TxHash, deposit.TxHash);
        Assert.Equal(DepositStatuses.Credited, deposit.Status);
        Assert.NotNull(deposit.CreditedAt);
    }

    [SkippableFact]
    public async Task A_shallow_deposit_is_visible_and_not_yet_money()
    {
        Skip.IfNot(_fixture.Available, "No Keycloak or no Postgres reachable.");

        await using var host = _fixture.Build();
        var user = await VerifiedUserAsync(host);
        var address = (await AddressAsync(host, user)).Address;

        await using (var scope = host.Services.CreateAsyncScope())
        {
            var custody = scope.ServiceProvider.GetRequiredService<CustodyService>();
            await custody.ObserveAsync(new ObservedTransfer(
                Network: Chain,
                Address: address,
                TxHash: "0x" + Guid.NewGuid().ToString("N"),
                Amount: Money.Parse("10.000000", TestCurrencies.Usdt),
                Confirmations: 4));
        }

        using var client = host.CreateClient();
        Authorize(client, user.AccessToken);
        var deposits = await Read<List<DepositDto>>(
            await client.GetAsync(new Uri("/v1/me/custody/deposits", UriKind.Relative)));

        // Shown, so somebody watching a block explorer is not left wondering
        // whether IslaPay has noticed. Not credited, because four blocks deep
        // is a transfer that can still be un-happened.
        var pending = Assert.Single(deposits);
        Assert.Equal(DepositStatuses.Confirming, pending.Status);
        Assert.Equal(4, pending.Confirmations);

        var ledger = host.Services.GetRequiredService<ILedger>();
        var balance = await ledger.BalanceOfAsync(
            AccountRef.User(user.UserId, TestCurrencies.Usdt));
        Assert.Equal("0.000000", balance.ToString());
    }

    [SkippableFact]
    public async Task The_networks_this_build_watches_are_published()
    {
        Skip.IfNot(_fixture.Available, "No Keycloak or no Postgres reachable.");

        await using var host = _fixture.Build();
        var user = await VerifiedUserAsync(host);

        using var client = host.CreateClient();
        Authorize(client, user.AccessToken);
        var body = await (await client.GetAsync(
            new Uri("/v1/me/custody/networks", UriKind.Relative))).Content.ReadAsStringAsync();

        // So a client does not hard-code a list it cannot keep in step with
        // the server's — and, more to the point, does not offer a network
        // nothing is watching.
        Assert.Contains("\"id\":\"tron\"", body, StringComparison.Ordinal);
        Assert.Contains("\"currency\":\"USDT\"", body, StringComparison.Ordinal);
        Assert.Contains("\"confirmations\":19", body, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------ helpers

    private sealed record Account(string UserId, string Email, string Phone, string AccessToken);

    /// <summary>What the running host's catalogue says about USDT on TRON.</summary>
    private static CurrencyOnNetwork Pair(IslaPayHost host) =>
        host.Services.GetRequiredService<ICurrencyCatalog>().OnNetwork(Asset, Chain)!;

    private static async Task<DepositAddressDto> AddressAsync(IslaPayHost host, Account user)
    {
        using var client = host.CreateClient();
        Authorize(client, user.AccessToken);
        return await Read<DepositAddressDto>(await client.GetAsync(
            new Uri($"/v1/me/custody/addresses/{Asset}/{Chain}", UriKind.Relative)));
    }

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

    private static async Task<Account> VerifiedUserAsync(IslaPayHost host)
    {
        var account = await RegisterAsync(host);

        using var client = host.CreateClient();
        Authorize(client, account.AccessToken);
        var code = host.Codes.CodeFor(OtpPurpose.PhoneVerification, account.Phone);
        var done = await client.PostAsJsonAsync(
            "/v1/auth/otp/verify", new OtpVerifyRequest(account.Phone, code), Json);

        Assert.True(done.IsSuccessStatusCode, await done.Content.ReadAsStringAsync());
        return account;
    }

    private static void Authorize(HttpClient client, string token) =>
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

    private static async Task<T> Read<T>(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync();
        return JsonSerializer.Deserialize<T>(body, Json)
            ?? throw new InvalidOperationException($"Could not read a {typeof(T).Name} from: {body}");
    }
}
