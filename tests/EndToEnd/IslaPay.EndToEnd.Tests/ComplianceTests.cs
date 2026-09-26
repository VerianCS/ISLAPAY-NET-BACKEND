using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using IslaPay.Identity;
using IslaPay.Identity.Contracts;
using IslaPay.Ledger.Contracts;
using IslaPay.Platform;
using IslaPay.Platform.Api;
using IslaPay.Platform.AspNet;
using IslaPay.Platform.AspNet.Security;
using IslaPay.Platform.Serialization;
using IslaPay.TestSupport;
using IslaPay.Wallet.Contracts;
using Microsoft.Extensions.DependencyInjection;

namespace IslaPay.EndToEnd.Tests;

/// <summary>
/// Freezing an account and setting its level, through the host and a real
/// Keycloak, and the modules obeying both on the next request.
/// </summary>
[Collection(IslaPayHostDefinition.Name)]
[Trait("Category", "Integration")]
public class ComplianceTests
{
    private const string Password = "Correct-Horse-9";
    private static readonly JsonSerializerOptions Json = IslaPayJson.Create(TestCurrencies.Scales);

    private readonly IslaPayHostFixture _fixture;

    public ComplianceTests(IslaPayHostFixture fixture) => _fixture = fixture;

    [SkippableFact]
    public async Task A_frozen_account_moves_nothing_until_it_is_unfrozen()
    {
        Skip.IfNot(_fixture.Available, "No Keycloak or no Postgres reachable.");

        await using var host = _fixture.Build();
        var customer = await CustomerAsync(host);
        var friend = await CustomerAsync(host);
        await FundAsync(host, customer, "50.00");
        using var compliance = Client(host, await StaffAsync(host, StaffRoles.Compliance));

        // Support's lookup is by the address the person gives on the phone.
        var found = await Read<AccountStandingDto>(await compliance.GetAsync(
            new Uri($"/v1/admin/compliance/accounts?email={Uri.EscapeDataString(customer.Email)}", UriKind.Relative)));
        Assert.Equal(customer.UserId, found.UserId);
        Assert.Equal(1, found.Level);
        Assert.False(found.Frozen);

        var frozen = await Read<AccountStandingDto>(await compliance.PostAsJsonAsync(
            $"/v1/admin/compliance/accounts/{customer.UserId}/freeze",
            new StandingChangeRequest("Reporte de fraude 2026-114"), Json));
        Assert.True(frozen.Frozen);
        Assert.Equal("Reporte de fraude 2026-114", frozen.FrozenReason);

        // The next request, with the same token: the freeze is read, not cached.
        var refused = await TransferAsync(host, customer, friend.Email, "5.00");
        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        Assert.Equal(Standing.AccountFrozen, (await Problem(refused)).Code);

        // The person can still sign in and is told.
        var session = await SignInAsync(host, customer);
        Assert.True(session.User.Frozen);

        await Read<AccountStandingDto>(await compliance.PostAsJsonAsync(
            $"/v1/admin/compliance/accounts/{customer.UserId}/unfreeze",
            new StandingChangeRequest("Descartado tras llamada"), Json));
        Assert.Equal(HttpStatusCode.OK, (await TransferAsync(host, customer, friend.Email, "5.00")).StatusCode);
    }

    [SkippableFact]
    public async Task A_level_caps_one_movement_and_compliance_raises_it()
    {
        Skip.IfNot(_fixture.Available, "No Keycloak or no Postgres reachable.");

        await using var host = _fixture.Build();
        var customer = await CustomerAsync(host);
        var friend = await CustomerAsync(host);
        await FundAsync(host, customer, "3000.00");
        using var compliance = Client(host, await StaffAsync(host, StaffRoles.Compliance));

        var tooMuch = await TransferAsync(host, customer, friend.Email, "1500.00");
        Assert.Equal(HttpStatusCode.UnprocessableEntity, tooMuch.StatusCode);
        var problem = await Problem(tooMuch);
        Assert.Equal(Standing.LimitExceeded, problem.Code);
        Assert.Equal("1000", problem.Meta!["limit"].ToString());

        var raised = await Read<AccountStandingDto>(await compliance.PutAsJsonAsync(
            $"/v1/admin/compliance/accounts/{customer.UserId}/level",
            new LevelChangeRequest(2, "Carné verificado en oficina"), Json));
        Assert.Equal(2, raised.Level);
        Assert.Equal("25000", raised.MaxPerMovement);

        Assert.Equal(HttpStatusCode.OK, (await TransferAsync(host, customer, friend.Email, "1500.00")).StatusCode);
    }

    [SkippableFact]
    public async Task Support_looks_and_cannot_touch_and_every_change_needs_a_reason()
    {
        Skip.IfNot(_fixture.Available, "No Keycloak or no Postgres reachable.");

        await using var host = _fixture.Build();
        var customer = await CustomerAsync(host);
        using var support = Client(host, await StaffAsync(host, StaffRoles.Support));
        using var compliance = Client(host, await StaffAsync(host, StaffRoles.Compliance));

        Assert.Equal(HttpStatusCode.OK, (await support.GetAsync(
            new Uri($"/v1/admin/compliance/accounts/{customer.UserId}", UriKind.Relative))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await support.PostAsJsonAsync(
            $"/v1/admin/compliance/accounts/{customer.UserId}/freeze",
            new StandingChangeRequest("Porque sí"), Json)).StatusCode);

        var bare = await compliance.PostAsJsonAsync(
            $"/v1/admin/compliance/accounts/{customer.UserId}/freeze", new StandingChangeRequest(""), Json);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, bare.StatusCode);
        Assert.Equal(IdentityErrors.InvalidStandingChange, (await Problem(bare)).Code);

        var nobody = await compliance.GetAsync(
            new Uri("/v1/admin/compliance/accounts?email=nadie%40islapay.cu", UriKind.Relative));
        Assert.Equal(HttpStatusCode.NotFound, nobody.StatusCode);
    }

    [SkippableFact]
    public async Task Identity_is_checked_only_after_the_phone()
    {
        Skip.IfNot(_fixture.Available, "No Keycloak or no Postgres reachable.");

        await using var host = _fixture.Build();
        var unverified = await RegisterAsync(host);
        using var compliance = Client(host, await StaffAsync(host, StaffRoles.Compliance));

        var refused = await compliance.PutAsJsonAsync(
            $"/v1/admin/compliance/accounts/{unverified.UserId}/level",
            new LevelChangeRequest(2, "Documento visto"), Json);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, refused.StatusCode);
    }

    // ---------------------------------------------------------------- helpers

    private sealed record Account(string UserId, string Email, string Phone, string AccessToken);

    private static async Task<Account> RegisterAsync(IslaPayHost host)
    {
        using var client = host.CreateClient();
        var id = Guid.NewGuid().ToString("N")[..10];
        var email = $"kyc-{id}@islapay.cu";
        var phone = "+53" + Random.Shared.NextInt64(500_000_000, 599_999_999)
            .ToString(System.Globalization.CultureInfo.InvariantCulture);

        var registered = await client.PostAsJsonAsync(
            "/v1/auth/register", new RegisterRequest("Ana Pérez", email, phone, Password), Json);
        var session = await Read<AuthSessionResponse>(registered);
        return new Account(session.User.Id, email, phone, session.Tokens.AccessToken);
    }

    private static async Task<Account> CustomerAsync(IslaPayHost host)
    {
        var account = await RegisterAsync(host);
        using var client = Client(host, account);
        var code = host.Codes.CodeFor(OtpPurpose.PhoneVerification, account.Phone);
        var done = await client.PostAsJsonAsync(
            "/v1/auth/otp/verify", new OtpVerifyRequest(account.Phone, code), Json);
        Assert.True(done.IsSuccessStatusCode, await done.Content.ReadAsStringAsync());
        return account;
    }

    private async Task<Account> StaffAsync(IslaPayHost host, string role)
    {
        var account = await RegisterAsync(host);
        await _fixture.Keycloak.GrantRealmRoleAsync(account.UserId, role);
        var session = await SignInAsync(host, account);
        return account with { AccessToken = session.Tokens.AccessToken };
    }

    private static async Task<AuthSessionResponse> SignInAsync(IslaPayHost host, Account account)
    {
        using var client = host.CreateClient();
        return await Read<AuthSessionResponse>(await client.PostAsJsonAsync(
            "/v1/auth/login", new LoginRequest(account.Email, Password), Json));
    }

    /// <summary>Money in the customer's wallet, straight from the float.</summary>
    private static async Task FundAsync(IslaPayHost host, Account account, string amount)
    {
        using var scope = host.Services.CreateScope();
        var ledger = scope.ServiceProvider.GetRequiredService<ILedger>();
        var money = Money.Parse(amount, TestCurrencies.EIsla);
        await ledger.PostAsync(new PostingRequest(
            Kind: "settlement",
            Legs:
            [
                new PostingLeg(AccountRef.User(account.UserId, TestCurrencies.EIsla), money),
                new PostingLeg(AccountRef.CashFloat(TestCurrencies.EIsla), -money),
            ],
            IdempotencyKey: $"e2e:kyc:{Guid.NewGuid():N}"));
    }

    private static async Task<HttpResponseMessage> TransferAsync(
        IslaPayHost host, Account sender, string destination, string amount)
    {
        using var client = Client(host, sender);
        using var request = new HttpRequestMessage(HttpMethod.Post, "/v1/transfers")
        {
            Content = JsonContent.Create(
                new TransferRequest(Money.Parse(amount, TestCurrencies.EIsla), destination), options: Json),
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
