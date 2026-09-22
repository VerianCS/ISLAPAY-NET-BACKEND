using IslaPay.TestSupport;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using IslaPay.Catalog.Contracts;
using IslaPay.Identity;
using IslaPay.Identity.Contracts;
using IslaPay.Ledger.Contracts;
using IslaPay.Marketplace.Contracts;
using IslaPay.Platform;
using IslaPay.Platform.Api;
using IslaPay.Platform.AspNet;
using IslaPay.Platform.Serialization;
using IslaPay.Wallet.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Xunit.Abstractions;

namespace IslaPay.EndToEnd.Tests;

/// <summary>
/// One person buying something from another, from nothing, in one go.
/// </summary>
/// <remarks>
/// <para>
/// Every other test here checks a slice: that escrow empties, that a retry
/// locks one hold, that a stranger cannot spend the code. Each one builds the
/// state it needs and asserts the one thing it is about. That is the right
/// shape for a regression suite and it is not an answer to "does the whole
/// thing work" — a system can pass every slice and still have no journey a
/// real person can complete.
/// </para>
/// <para>
/// So this is the journey, end to end and in order: two accounts registered,
/// a phone proved, a password signed in with, money in, an item published,
/// browsed, paid for, collected. Nothing is set up behind the API's back
/// except the one thing that has no endpoint yet — putting money into a new
/// account — and that is called out where it happens.
/// </para>
/// <para>
/// It narrates as it goes. Run it with <c>-l "console;verbosity=detailed"</c>
/// and the output is the transcript of the journey with the real figures in
/// it, which is what somebody asking "show me it working" actually wants.
/// </para>
/// </remarks>
[Collection(IslaPayHostDefinition.Name)]
[Trait("Category", "Integration")]
public class PurchaseJourneyTests
{
    private const string Password = "Correct-Horse-9";
    private static readonly JsonSerializerOptions Json =
        IslaPayJson.Create(TestCurrencies.Scales);

    private readonly IslaPayHostFixture _fixture;
    private readonly ITestOutputHelper _out;

    public PurchaseJourneyTests(IslaPayHostFixture fixture, ITestOutputHelper output)
    {
        _fixture = fixture;
        _out = output;
    }

    [SkippableFact]
    public async Task From_two_strangers_to_a_completed_purchase()
    {
        Skip.IfNot(_fixture.Available, "No Keycloak or no Postgres reachable.");

        await using var host = _fixture.Build();
        var ledger = host.Services.GetRequiredService<ILedger>();

        // ---------------------------------------------------------- 1. the unit
        //
        // What a client does first, before it can render a single amount: ask
        // what money there is. The scale is the one fact the wire does not
        // carry — "50.00" is a different number of minor units in a two-place
        // currency and a six-place one.
        var catalog = host.Services.GetRequiredService<ICurrencyCatalog>();
        var eIsla = catalog.Require(CurrencyCodes.EIsla);
        Step($"El catálogo dice que {eIsla.Code} lleva {eIsla.Scale} decimales.");

        Assert.Equal(2, eIsla.Scale);
        Assert.Contains(catalog.Holdable, c => c.Code == CurrencyCodes.EIsla);

        // ------------------------------------------------- 2. two registrations
        var buyer = await RegisterAsync(host, "Ana Pérez");
        var seller = await RegisterAsync(host, "Luis Mena");
        Step($"Registrados: Ana ({buyer.Email}) y Luis ({seller.Email}).");

        // --------------------------------------------------- 3. proving a phone
        //
        // The gate on every way money moves. Luis can list without it — an
        // advertisement is not a payment — but neither of them can trade.
        await VerifyPhoneAsync(host, buyer);
        await VerifyPhoneAsync(host, seller);
        Step("Ambos teléfonos verificados por OTP.");

        // --------------------------------------------------------- 4. a login
        //
        // A real second sign-in with the password, not the token registration
        // handed back. This is the session a returning user gets, and it is
        // the one the rest of the journey is made with.
        buyer = await LogInAsync(host, buyer);
        seller = await LogInAsync(host, seller);
        Step("Sesión iniciada con contraseña; el resto del recorrido usa ese token.");

        // ------------------------------------------------------- 5. money in
        //
        // The one step with no endpoint behind it. There is no deposit or
        // recharge route yet, so this posts the settlement a recharge will
        // post when it exists: the platform's cash float pays the customer.
        // Called out rather than hidden, because it is the seam between what
        // works and what does not.
        var funded = Money.Parse("60.00", eIsla);
        await ledger.PostAsync(new PostingRequest(
            Kind: "settlement",
            Legs:
            [
                new PostingLeg(AccountRef.User(buyer.UserId, eIsla), funded),
                new PostingLeg(AccountRef.CashFloat(eIsla), -funded),
            ],
            Metadata: new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["method"] = "journey",
            }));
        Step($"Ana recibe {funded} del flotante. (Único paso sin endpoint: "
            + "todavía no hay recarga.)");

        Assert.Equal("60.00", (await BalanceAsync(ledger, buyer.UserId, eIsla)).ToString());

        // --------------------------------------------------- 6. an advertisement
        using (var client = Client(host, seller))
        {
            var published = await client.PostAsJsonAsync(
                "/v1/listings",
                new PublishListingRequest(
                    Title: "Bicicleta Cannondale",
                    Description: "Poco uso, frenos nuevos",
                    Category: "Deportes",
                    Condition: "Como nuevo",
                    Price: Money.Parse("50.00", eIsla),
                    Location: "Vedado, La Habana"),
                Json);

            Assert.True(published.IsSuccessStatusCode,
                await published.Content.ReadAsStringAsync());
            _listing = await Read<ListingDto>(published);
        }

        Step($"Luis publica «{_listing.Title}» a {_listing.Price}.");
        Assert.Equal(ListingStatuses.Active, _listing.Status);

        // ---------------------------------------------------------- 7. browsing
        //
        // From the buyer's side, through the public list rather than by id:
        // an item nobody can find is an item nobody buys.
        using (var client = Client(host, buyer))
        {
            var page = await Read<CursorPage<ListingDto>>(
                await client.GetAsync(new Uri("/v1/listings?limit=50", UriKind.Relative)));

            Assert.Contains(page.Items, l => l.Id == _listing.Id);
            Step($"Ana lo encuentra navegando: {page.Items.Count} anuncio(s) activos.");
        }

        // ------------------------------------------------------ 8. locking a price
        //
        // The money leaves Ana's account now, not when Luis collects. A
        // reservation that left the balance alone would let the same 60 be
        // promised to three sellers.
        // Escrow is one account for the whole platform, so what this step
        // proves is the movement, not the total: another test in this
        // collection may legitimately be holding money in it at the same time.
        var escrowBefore = await EscrowAsync(host, eIsla);

        var order = await Read<OrderDto>(
            await PostAsync(host, buyer, "/v1/orders",
                new PlaceOrderRequest(Guid.Parse(_listing.Id))));

        var afterHold = await BalanceAsync(ledger, buyer.UserId, eIsla);
        var escrow = await EscrowAsync(host, eIsla);
        Step($"Ana bloquea el precio. Su saldo: {afterHold}. En escrow: {escrow}.");

        // `held`, not `pending`: pending means the hold posting may not have
        // landed yet, and by the time the response comes back it has.
        Assert.Equal(OrderStatuses.Held, order.Status);
        Assert.Equal("10.00", afterHold.ToString());
        Assert.Equal(escrowBefore.MinorUnits + 5000, escrow.MinorUnits);
        Assert.NotNull(order.Code);

        // The code is the buyer's proof of payment and nobody else's. Luis
        // being able to read it would mean he could collect without Ana ever
        // handing the bike over.
        using (var client = Client(host, seller))
        {
            var seen = await Read<OrderDto>(await client.GetAsync(
                new Uri($"/v1/orders/{order.Id}", UriKind.Relative)));
            Assert.Null(seen.Code);
            Step("Luis puede ver el pedido y no el código. Sólo Ana lo tiene.");
        }

        // ------------------------------------------------------- 9. collecting
        var released = await Read<OrderDto>(
            await PostAsync(host, seller, "/v1/orders/redeem",
                new RedeemOrderRequest(order.Code!)));

        var sellerBalance = await BalanceAsync(ledger, seller.UserId, eIsla);
        var buyerBalance = await BalanceAsync(ledger, buyer.UserId, eIsla);
        var leftInEscrow = await EscrowAsync(host, eIsla);

        Step($"Luis escanea el código. Cobra {sellerBalance}; Ana se queda con "
            + $"{buyerBalance}; en escrow quedan {leftInEscrow}.");

        Assert.Equal(OrderStatuses.Released, released.Status);
        // 1% de comisión: 50.00 menos 0.50.
        Assert.Equal("49.50", sellerBalance.ToString());
        Assert.Equal("10.00", buyerBalance.ToString());
        // Back where it started: this order's 50.00 left escrow, and whatever
        // was there before this test began is still there.
        Assert.Equal(escrowBefore.MinorUnits, leftInEscrow.MinorUnits);

        // Nada se perdió por el camino: lo que salió de Ana está entre Luis y
        // la comisión. Es lo que un libro de doble entrada garantiza, y lo que
        // un test que sólo mire un saldo no comprueba.
        var fee = await FeesAsync(host, eIsla);
        Assert.Equal("0.50", fee.ToString());
        Step($"La comisión del 1% ({fee}) está en la cuenta de ingresos.");

        // -------------------------------------------- 10. both wallets agree
        //
        // The screen each of them actually looks at, not the ledger behind it.
        // A journey that balances in the database and shows nothing in the app
        // is a journey neither of them can believe.
        var buyerWallet = await WalletAsync(host, buyer);
        var sellerWallet = await WalletAsync(host, seller);

        Assert.Contains(buyerWallet.Transactions.Items,
            t => t.Amount.ToString() == "-50.00");
        Assert.Contains(sellerWallet.Transactions.Items,
            t => t.Amount.ToString() == "49.50");

        Step($"La billetera de Ana muestra {buyerWallet.Transactions.Items.Count} "
            + $"movimiento(s); la de Luis, {sellerWallet.Transactions.Items.Count}.");
        Step("Recorrido completo: de dos desconocidos a una compra cobrada.");
    }

    private ListingDto _listing = null!;

    // ---------------------------------------------------------------- helpers

    private void Step(string line) => _out.WriteLine($"  · {line}");

    private sealed record Account(
        string UserId, string Email, string Phone, string AccessToken);

    private static async Task<Account> RegisterAsync(IslaPayHost host, string name)
    {
        using var client = host.CreateClient();
        var id = Guid.NewGuid().ToString("N")[..8];
        var email = $"viaje-{id}@islapay.cu";
        var phone = "+53" + Random.Shared.NextInt64(500_000_000, 599_999_999)
            .ToString(System.Globalization.CultureInfo.InvariantCulture);

        var response = await client.PostAsJsonAsync(
            "/v1/auth/register", new RegisterRequest(name, email, phone, Password), Json);

        Assert.True(response.IsSuccessStatusCode,
            $"register: {await response.Content.ReadAsStringAsync()}");

        var session = await Read<AuthSessionResponse>(response);
        return new Account(session.User.Id, email, phone, session.Tokens.AccessToken);
    }

    private static async Task VerifyPhoneAsync(IslaPayHost host, Account account)
    {
        using var client = Client(host, account);
        var code = host.Codes.CodeFor(OtpPurpose.PhoneVerification, account.Phone);
        var response = await client.PostAsJsonAsync(
            "/v1/auth/otp/verify", new OtpVerifyRequest(account.Phone, code), Json);

        Assert.True(response.IsSuccessStatusCode,
            $"verify: {await response.Content.ReadAsStringAsync()}");
    }

    private static async Task<Account> LogInAsync(IslaPayHost host, Account account)
    {
        using var client = host.CreateClient();
        var response = await client.PostAsJsonAsync(
            "/v1/auth/login", new LoginRequest(account.Email, Password), Json);

        Assert.True(response.IsSuccessStatusCode,
            $"login: {await response.Content.ReadAsStringAsync()}");

        var session = await Read<AuthSessionResponse>(response);
        return account with { AccessToken = session.Tokens.AccessToken };
    }

    private static async Task<WalletResponse> WalletAsync(IslaPayHost host, Account who)
    {
        using var client = Client(host, who);
        return await Read<WalletResponse>(
            await client.GetAsync(new Uri("/v1/me/wallet", UriKind.Relative)));
    }

    private static HttpClient Client(IslaPayHost host, Account who)
    {
        var client = host.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", who.AccessToken);
        return client;
    }

    private static async Task<HttpResponseMessage> PostAsync(
        IslaPayHost host, Account who, string path, object body)
    {
        using var client = Client(host, who);
        using var request = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = new StringContent(
                JsonSerializer.Serialize(body, Json), Encoding.UTF8, "application/json"),
        };
        request.Headers.Add(IdempotencyMiddleware.HeaderName, Guid.NewGuid().ToString("N"));

        var response = await client.SendAsync(request);
        Assert.True(response.IsSuccessStatusCode,
            $"{path}: {(int)response.StatusCode} {await response.Content.ReadAsStringAsync()}");
        return response;
    }

    private static async Task<Money> BalanceAsync(
        ILedger ledger, string userId, Currency currency)
    {
        var balances = await ledger.BalancesAsync(userId);
        return balances.FirstOrDefault(b => b.Currency == currency)?.Balance
            ?? Money.Zero(currency);
    }

    private static Task<Money> EscrowAsync(IslaPayHost host, Currency currency) =>
        PlatformBalanceAsync(host, "escrow", currency);

    private static Task<Money> FeesAsync(IslaPayHost host, Currency currency) =>
        PlatformBalanceAsync(host, "fees", currency);

    private static async Task<Money> PlatformBalanceAsync(
        IslaPayHost host, string account, Currency currency)
    {
        var database = host.Services.GetRequiredService<Platform.Data.IDatabase>();
        await using var connection = await database.OpenAsync();
        await using var command = new Npgsql.NpgsqlCommand("""
            SELECT coalesce(sum(e.minor_units), 0)::bigint
            FROM ledger.entries e
            JOIN ledger.accounts a ON a.id = e.account_id
            WHERE a.owner_type = 'platform' AND a.owner = @owner AND e.currency = @currency;
            """, connection);
        command.Parameters.AddWithValue("owner", account);
        command.Parameters.AddWithValue("currency", currency.Code);

        return Money.FromMinorUnits((long)(await command.ExecuteScalarAsync())!, currency);
    }

    private static async Task<T> Read<T>(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync();
        return JsonSerializer.Deserialize<T>(body, Json)
            ?? throw new InvalidOperationException($"No pude leer un {typeof(T).Name} de: {body}");
    }
}
