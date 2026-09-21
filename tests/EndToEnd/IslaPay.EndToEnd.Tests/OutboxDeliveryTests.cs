using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using IslaPay.Identity;
using IslaPay.Identity.Contracts;
using IslaPay.Ledger.Contracts;
using IslaPay.Marketplace.Contracts;
using IslaPay.Platform;
using IslaPay.Platform.AspNet;
using IslaPay.Platform.Serialization;
using Microsoft.Extensions.DependencyInjection;

namespace IslaPay.EndToEnd.Tests;

/// <summary>
/// That what the outbox holds actually leaves it.
/// </summary>
/// <remarks>
/// Every other test in this suite runs with <c>Outbox:PublishInBackground</c>
/// off and drains explicitly where it cares, which means a module's events can
/// be written, never published, and nothing fails. That is the gap this
/// closes: it publishes for real, against a real broker, and then asks the
/// outbox whether the row was marked.
/// </remarks>
[Collection(IslaPayHostDefinition.Name)]
[Trait("Category", "Integration")]
public class OutboxDeliveryTests
{
    private const string Password = "Correct-Horse-9";
    private static readonly JsonSerializerOptions Json = IslaPayJson.Options;

    private readonly IslaPayHostFixture _fixture;

    public OutboxDeliveryTests(IslaPayHostFixture fixture) => _fixture = fixture;

    [SkippableFact]
    public async Task Every_context_that_publishes_can_actually_publish()
    {
        Skip.IfNot(_fixture.Available, "No Keycloak or no Postgres reachable.");

        await using var host = _fixture.Build();

        var seller = await VerifiedUserAsync(host);
        var buyer = await VerifiedUserAsync(host);
        await FundAsync(host, buyer.UserId, "40.00");

        // Registration publishes from `identity`, which has a consumer and so
        // has a declared exchange. Ordering publishes from `marketplace`,
        // which has none.
        var listing = await PublishListingAsync(host, seller, "10.00");
        await PlaceOrderAsync(host, buyer, listing);

        await DrainAsync(host);

        var stuck = await UnpublishedRoutingKeysAsync(host);

        // A routing key left here is an event the rest of the system will
        // never see. It is not a delivery that is merely late: the publisher
        // has already tried and given up on it.
        Assert.True(
            stuck.Count == 0,
            $"The outbox could not publish: {string.Join(", ", stuck)}");
    }

    // ---------------------------------------------------------------- helpers

    private sealed record Account(string UserId, string Email, string Phone, string AccessToken);

    private static async Task DrainAsync(IslaPayHost host)
    {
        await host.Services.GetRequiredService<Platform.Messaging.MessagingTopology>()
            .Declared.WaitAsync(TimeSpan.FromSeconds(30));
        await host.Services.GetRequiredService<Platform.Messaging.OutboxPublisher>()
            .DrainOnceAsync();
    }

    private static async Task<List<string>> UnpublishedRoutingKeysAsync(IslaPayHost host)
    {
        var database = host.Services.GetRequiredService<Platform.Data.IDatabase>();
        await using var connection = await database.OpenAsync();
        await using var command = new Npgsql.NpgsqlCommand(
            "SELECT context || ':' || routing_key FROM messaging.outbox WHERE published_at IS NULL;",
            connection);

        var keys = new List<string>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync()) keys.Add(reader.GetString(0));
        return keys;
    }

    private static async Task<Guid> PublishListingAsync(
        IslaPayHost host, Account seller, string price)
    {
        using var client = host.CreateClient();
        Authorize(client, seller.AccessToken);

        var response = await client.PostAsJsonAsync(
            "/v1/listings",
            new PublishListingRequest(
                Title: "Ventilador",
                Description: null,
                Category: "Hogar",
                Condition: "Usado",
                Price: Money.Parse(price, Currency.EIsla)),
            Json);

        response.EnsureSuccessStatusCode();
        return Guid.Parse((await response.Content.ReadFromJsonAsync<ListingDto>(Json))!.Id);
    }

    private static async Task PlaceOrderAsync(IslaPayHost host, Account buyer, Guid listing)
    {
        using var client = host.CreateClient();
        Authorize(client, buyer.AccessToken);

        using var request = new HttpRequestMessage(HttpMethod.Post, "/v1/orders")
        {
            Content = new StringContent(
                JsonSerializer.Serialize(new PlaceOrderRequest(listing), Json),
                Encoding.UTF8,
                "application/json"),
        };
        request.Headers.Add(IdempotencyMiddleware.HeaderName, Guid.NewGuid().ToString("N"));

        (await client.SendAsync(request)).EnsureSuccessStatusCode();
    }

    private static async Task<Account> VerifiedUserAsync(IslaPayHost host)
    {
        using var client = host.CreateClient();
        var email = $"outbox-{Guid.NewGuid():N}@example.test";
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

    private static async Task FundAsync(IslaPayHost host, string userId, string amount)
    {
        var money = Money.Parse(amount, Currency.EIsla);
        await host.Services.GetRequiredService<ILedger>().PostAsync(new PostingRequest(
            Kind: "settlement",
            Legs:
            [
                new PostingLeg(AccountRef.User(userId, Currency.EIsla), money),
                new PostingLeg(AccountRef.CashFloat(Currency.EIsla), -money),
            ]));
    }

    private static void Authorize(HttpClient client, string accessToken) =>
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", accessToken);
}
