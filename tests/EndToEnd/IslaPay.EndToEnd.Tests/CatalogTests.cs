using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using IslaPay.Catalog;
using IslaPay.Catalog.Contracts;
using IslaPay.Identity;
using IslaPay.Identity.Contracts;
using IslaPay.Ledger.Contracts;
using IslaPay.Platform;
using IslaPay.Platform.Api;
using IslaPay.Platform.Serialization;
using IslaPay.TestSupport;
using Microsoft.Extensions.DependencyInjection;

namespace IslaPay.EndToEnd.Tests;

/// <summary>
/// The currencies and chains, through the running host.
/// </summary>
/// <remarks>
/// <para>
/// The point of the whole change, proved rather than argued: a currency is
/// switched on by writing a row, and the running process starts accepting it.
/// No deploy, no restart, no recompile. While <c>Currency</c> was an enum this
/// test could not have been written — not because it would have failed, but
/// because there was nothing to write it against.
/// </para>
/// <para>
/// The other half is the refusal. A catalogue that only ever said yes would
/// be a list; what makes it a control is that the ledger will not write an
/// entry in a currency it does not allow, and these check that from outside.
/// </para>
/// </remarks>
[Collection(IslaPayHostDefinition.Name)]
[Trait("Category", "Integration")]
public class CatalogTests
{
    private const string Password = "Correct-Horse-9";
    private static readonly JsonSerializerOptions Json = IslaPayJson.Create(TestCurrencies.Scales);

    private readonly IslaPayHostFixture _fixture;

    public CatalogTests(IslaPayHostFixture fixture) => _fixture = fixture;

    [SkippableFact]
    public async Task The_currencies_a_client_may_use_come_from_the_table()
    {
        Skip.IfNot(_fixture.Available, "No Keycloak or no Postgres reachable.");

        await using var host = _fixture.Build();
        var user = await VerifiedUserAsync(host);

        using var client = host.CreateClient();
        Authorize(client, user.AccessToken);
        var listed = await Read<List<CurrencyView>>(
            await client.GetAsync(new Uri("/v1/catalog/currencies", UriKind.Relative)));

        // Exactly the seeded, enabled set — and the scales the client needs to
        // render an amount, which is the one fact it cannot derive.
        Assert.Equal(
            ["EISLA", "USDT", "USDC", "CUP"],
            listed.Select(c => c.Code).ToArray());
        Assert.Equal(6, listed.Single(c => c.Code == "USDT").Scale);
        Assert.Equal(2, listed.Single(c => c.Code == "EISLA").Scale);

        // The peso is real and is not a wallet: it is the local leg of a P2P
        // trade, which IslaPay owes and nobody holds.
        Assert.False(listed.Single(c => c.Code == "CUP").CustomerHoldable);

        // Nothing switched off leaks into the list a client offers.
        Assert.DoesNotContain(listed, c => c.Code == "MXN");
        Assert.DoesNotContain(listed, c => c.Code == "USD");
    }

    /// <summary>
    /// A currency is switched on by an operator, and the running host starts
    /// taking it.
    /// </summary>
    /// <remarks>
    /// This is the test the enum made impossible. Nothing is deployed between
    /// the refusal and the acceptance — the same process, the same ledger, one
    /// row changed.
    /// </remarks>
    [SkippableFact]
    public async Task A_currency_switched_on_becomes_usable_without_a_deploy()
    {
        Skip.IfNot(_fixture.Available, "No Keycloak or no Postgres reachable.");

        await using var host = _fixture.Build();
        var operador = await CatalogAdminAsync(host);
        var ledger = host.Services.GetRequiredService<ILedger>();
        var catalog = host.Services.GetRequiredService<ICurrencyCatalog>();

        // Listed and off. The ledger will not write an entry in it.
        var mexican = Currency.Of("MXN", 2);
        await Assert.ThrowsAsync<UnknownCurrencyException>(
            () => ledger.EnsureUserAccountsAsync(operador.UserId, [mexican]));

        using var client = host.CreateClient();
        Authorize(client, operador.AccessToken);
        var flipped = await client.PutAsync(
            new Uri("/v1/admin/catalog/currencies/MXN/enabled?value=true", UriKind.Relative),
            content: null);

        Assert.Equal(HttpStatusCode.NoContent, flipped.StatusCode);

        // Same process, same objects. The switch took effect because the
        // admin service refreshes the snapshot, not because anything restarted.
        Assert.Equal(mexican, catalog.Require("MXN"));
        await ledger.EnsureUserAccountsAsync(operador.UserId, [mexican]);
        Assert.Equal(
            "0.00",
            (await ledger.BalanceOfAsync(AccountRef.User(operador.UserId, mexican))).ToString());

        // Put back, so the next test sees the seed it expects.
        await client.PutAsync(
            new Uri("/v1/admin/catalog/currencies/MXN/enabled?value=false", UriKind.Relative),
            content: null);
    }

    [SkippableFact]
    public async Task Only_an_operator_may_flip_a_switch()
    {
        Skip.IfNot(_fixture.Available, "No Keycloak or no Postgres reachable.");

        await using var host = _fixture.Build();
        var ordinary = await VerifiedUserAsync(host);

        using var client = host.CreateClient();
        Authorize(client, ordinary.AccessToken);
        var refused = await client.PutAsync(
            new Uri("/v1/admin/catalog/currencies/MXN/enabled?value=true", UriKind.Relative),
            content: null);

        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
    }

    /// <summary>
    /// The ledger refuses a posting in a currency the catalogue does not
    /// allow, whichever module asks.
    /// </summary>
    /// <remarks>
    /// Checked at the ledger rather than at each caller on purpose: it is the
    /// one place every movement of money passes through, so it is the only
    /// place the rule can be a guarantee rather than a convention six modules
    /// each keep separately.
    /// </remarks>
    [SkippableFact]
    public async Task The_ledger_refuses_a_currency_that_is_not_listed()
    {
        Skip.IfNot(_fixture.Available, "No Keycloak or no Postgres reachable.");

        await using var host = _fixture.Build();
        var user = await VerifiedUserAsync(host);
        var ledger = host.Services.GetRequiredService<ILedger>();

        var invented = Currency.Of("ZZZ", 2);
        var refused = await Assert.ThrowsAsync<UnknownCurrencyException>(() =>
            ledger.PostAsync(new PostingRequest(
                Kind: "transfer",
                Legs:
                [
                    new PostingLeg(
                        AccountRef.SettlementFund(invented),
                        Money.FromMinorUnits(-100, invented)),
                    new PostingLeg(
                        AccountRef.User(user.UserId, invented),
                        Money.FromMinorUnits(100, invented)),
                ])));

        Assert.Equal("ZZZ", refused.Code);
    }

    /// <summary>
    /// A leg whose scale disagrees with the table is refused, even though it
    /// balances.
    /// </summary>
    /// <remarks>
    /// The failure this catches is silent by nature: the legs sum to zero,
    /// every invariant the ledger checks holds, and the amount written is
    /// wrong by a factor of ten thousand. Only the catalogue knows.
    /// </remarks>
    [SkippableFact]
    public async Task The_ledger_refuses_an_amount_at_the_wrong_scale()
    {
        Skip.IfNot(_fixture.Available, "No Keycloak or no Postgres reachable.");

        await using var host = _fixture.Build();
        var user = await VerifiedUserAsync(host);
        var ledger = host.Services.GetRequiredService<ILedger>();

        // USDT is accounted in six places, and this says two.
        var wrong = Currency.Of("USDT", 2);
        var refused = await Assert.ThrowsAsync<UnknownCurrencyException>(() =>
            ledger.PostAsync(new PostingRequest(
                Kind: "transfer",
                Legs:
                [
                    new PostingLeg(
                        AccountRef.SettlementFund(wrong), Money.FromMinorUnits(-100, wrong)),
                    new PostingLeg(
                        AccountRef.User(user.UserId, wrong), Money.FromMinorUnits(100, wrong)),
                ])));

        Assert.Contains("6 decimal places", refused.Message, StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------- helpers

    private sealed record CurrencyView(
        string Code, string Name, int Scale, string Kind, string Symbol,
        bool CustomerHoldable, bool Enabled);

    private sealed record Account(string UserId, string Email, string Phone, string AccessToken);

    private async Task<Account> CatalogAdminAsync(IslaPayHost host)
    {
        var account = await VerifiedUserAsync(host);
        await _fixture.Keycloak.GrantRealmRoleAsync(account.UserId, CatalogModule.AdminRole);

        // Signed in again: roles are baked into a token when it is issued, and
        // the one from registration predates the grant.
        using var client = host.CreateClient();
        var response = await client.PostAsJsonAsync(
            "/v1/auth/login", new LoginRequest(account.Email, Password), Json);
        response.EnsureSuccessStatusCode();

        var session = (await response.Content.ReadFromJsonAsync<AuthSessionResponse>(Json))!;
        return account with { AccessToken = session.Tokens.AccessToken };
    }

    private static async Task<Account> VerifiedUserAsync(IslaPayHost host)
    {
        using var client = host.CreateClient();

        var id = Guid.NewGuid().ToString("N")[..10];
        var email = $"ana-{id}@islapay.cu";
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

    private static void Authorize(HttpClient client, string token) =>
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

    private static async Task<T> Read<T>(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync();
        return JsonSerializer.Deserialize<T>(body, Json)
            ?? throw new InvalidOperationException($"Could not read a {typeof(T).Name} from: {body}");
    }
}
