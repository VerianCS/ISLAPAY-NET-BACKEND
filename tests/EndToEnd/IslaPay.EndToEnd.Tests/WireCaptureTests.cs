using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using IslaPay.Identity;
using IslaPay.Identity.Contracts;
using IslaPay.Ledger.Contracts;
using IslaPay.Platform;
using IslaPay.Platform.AspNet;
using IslaPay.Platform.Serialization;
using IslaPay.Wallet.Contracts;
using Microsoft.Extensions.DependencyInjection;

namespace IslaPay.EndToEnd.Tests;

/// <summary>
/// Writes a real <c>GET /v1/me/wallet</c> response to disk, for the Flutter
/// repository's tests to parse.
/// </summary>
/// <remarks>
/// <para>
/// Skipped unless <c>WALLET_CAPTURE_PATH</c> is set, so it costs a normal run
/// nothing. It exists because a client test written against a body somebody
/// composed by hand tests that person's idea of the contract, not the
/// contract. The first capture disagreed with this repository twice: the rates
/// were still keyed <c>USD_USDC</c> after the E-ISLA rename and listed three
/// of six pairs, and a transfer's <c>meta</c> carries <c>to</c>,
/// <c>toName</c>, <c>from</c>, <c>fromName</c> and <c>note</c> rather than the
/// <c>destination</c> that <c>LedgerEntryTypes</c> documented. Neither failed
/// anything: a missing rate key reads as parity and a missing meta key reads
/// as a shorter sentence.
/// </para>
/// <para>
/// Run it after changing a wallet DTO and hand the file to the client:
/// <c>WALLET_CAPTURE_PATH=/tmp/wallet.json dotnet test
/// --filter FullyQualifiedName~WireCaptureTests</c>.
/// </para>
/// </remarks>
[Collection(IslaPayHostDefinition.Name)]
[Trait("Category", "Integration")]
public class WireCaptureTests
{
    private static readonly JsonSerializerOptions Json = IslaPayJson.Options;
    private readonly IslaPayHostFixture _fixture;

    public WireCaptureTests(IslaPayHostFixture fixture) => _fixture = fixture;

    [SkippableFact]
    public async Task Capture_the_wallet_response()
    {
        Skip.IfNot(_fixture.Available, "no deps");
        var into = Environment.GetEnvironmentVariable("WALLET_CAPTURE_PATH");
        Skip.If(string.IsNullOrEmpty(into), "no capture path");

        await using var host = _fixture.Build();

        var sender = await RegisterAsync(host, verify: true);
        var recipient = await RegisterAsync(host, verify: false);

        var money = Money.Parse("500.00", Currency.EIsla);
        await host.Services.GetRequiredService<ILedger>().PostAsync(new PostingRequest(
            Kind: "settlement",
            Legs:
            [
                new PostingLeg(AccountRef.User(sender.UserId, Currency.EIsla), money),
                new PostingLeg(AccountRef.CashFloat(Currency.EIsla), -money),
            ],
            Metadata: new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["method"] = "test",
            }));

        using var client = host.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", sender.AccessToken);

        using var transfer = new HttpRequestMessage(HttpMethod.Post, "/v1/transfers")
        {
            Content = new StringContent(
                JsonSerializer.Serialize(
                    new TransferRequest(Money.Parse("15.00", Currency.EIsla), recipient.Email),
                    Json),
                Encoding.UTF8, "application/json"),
        };
        transfer.Headers.Add(IdempotencyMiddleware.HeaderName, Guid.NewGuid().ToString("N"));
        var moved = await client.SendAsync(transfer);
        Assert.True(moved.IsSuccessStatusCode, await moved.Content.ReadAsStringAsync());

        var wallet = await client.GetAsync(new Uri("/v1/me/wallet", UriKind.Relative));
        var body = await wallet.Content.ReadAsStringAsync();
        Assert.True(wallet.IsSuccessStatusCode, body);

        await File.WriteAllTextAsync(into!, body);
        await File.WriteAllTextAsync(
            into + ".transfer", await moved.Content.ReadAsStringAsync());
    }

    private sealed record Account(string UserId, string Email, string AccessToken);

    private static async Task<Account> RegisterAsync(IslaPayHost host, bool verify)
    {
        using var client = host.CreateClient();
        var id = Guid.NewGuid().ToString("N")[..10];
        var email = $"ana-{id}@islapay.cu";
        var phone = "+53" + Random.Shared.NextInt64(500_000_000, 599_999_999)
            .ToString(System.Globalization.CultureInfo.InvariantCulture);

        var response = await client.PostAsJsonAsync(
            "/v1/auth/register",
            new RegisterRequest("Ana Pérez", email, phone, "Correct-Horse-9"), Json);
        var session = JsonSerializer.Deserialize<AuthSessionResponse>(
            await response.Content.ReadAsStringAsync(), Json)!;

        if (verify)
        {
            using var verifier = host.CreateClient();
            verifier.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Bearer", session.Tokens.AccessToken);
            var code = host.Codes.CodeFor(OtpPurpose.PhoneVerification, phone);
            var done = await verifier.PostAsJsonAsync(
                "/v1/auth/otp/verify", new OtpVerifyRequest(phone, code), Json);
            Assert.True(done.IsSuccessStatusCode, await done.Content.ReadAsStringAsync());
        }

        return new Account(session.User.Id, email, session.Tokens.AccessToken);
    }
}
