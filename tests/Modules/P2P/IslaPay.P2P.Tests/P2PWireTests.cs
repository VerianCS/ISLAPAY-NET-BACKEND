using IslaPay.TestSupport;
using System.Text.Json;
using IslaPay.P2P.Contracts;
using IslaPay.Platform;
using IslaPay.Platform.Serialization;

namespace IslaPay.P2P.Tests;

public class P2PWireTests
{
    private static readonly JsonSerializerOptions Json = IslaPayJson.Create(TestCurrencies.Scales);

    [Fact]
    public void Side_travels_as_a_name_not_an_ordinal()
    {
        var json = JsonSerializer.Serialize(
            new P2PTradeRequest(P2PSide.Sell, Money.Parse("100.00", TestCurrencies.Usdt), "cup_tm"),
            Json);

        Assert.Equal(
            """{"side":"sell","amount":{"amount":"100.000000","currency":"USDT"},"methodId":"cup_tm"}""",
            json);
    }

    [Theory]
    [InlineData("sell", P2PSide.Sell)]
    [InlineData("buy", P2PSide.Buy)]
    public void Side_round_trips(string wire, P2PSide expected)
    {
        var request = JsonSerializer.Deserialize<P2PTradeRequest>(
            $$"""{"side":"{{wire}}","amount":{"amount":"1.000000","currency":"USDT"},"methodId":"x"}""",
            Json);

        Assert.NotNull(request);
        Assert.Equal(expected, request!.Side);
    }

    [Fact]
    public void A_method_carries_both_sides_of_the_spread_per_wallet_currency()
    {
        var method = new P2PMethodDto(
            "cup", "CUP", "CUP", Available: true,
            Minimum: Money.Parse("500.00", TestCurrencies.Cup),
            Maximum: Money.Parse("60000.00", TestCurrencies.Cup),
            Rates:
            [
                new P2PMethodRateDto("EISLA", SellRate: "380", BuyRate: "390"),
                new P2PMethodRateDto("USDT", SellRate: null, BuyRate: "395"),
            ]);

        var json = JsonSerializer.Serialize(method, Json);

        // Two rates, not one. A single rate would have IslaPay trading against
        // itself at par and losing money on every round trip.
        Assert.Contains("\"sellRate\":\"380\"", json, StringComparison.Ordinal);
        Assert.Contains("\"buyRate\":\"390\"", json, StringComparison.Ordinal);
        Assert.Contains("\"available\":true", json, StringComparison.Ordinal);
        // A side that is not offered is absent, not a "0" a client could price with.
        Assert.Contains("{\"currency\":\"USDT\",\"buyRate\":\"395\"}", json, StringComparison.Ordinal);
        Assert.Contains("\"minimum\":{\"amount\":\"500.00\",\"currency\":\"CUP\"}", json, StringComparison.Ordinal);
    }

    [Fact]
    public void A_quoted_rate_is_optional_and_absent_when_unset()
    {
        // The client may send the rate it showed, and is refused if it moved.
        // A client that does not is filled at whatever is current, so the
        // field must not appear when it was not supplied.
        var json = JsonSerializer.Serialize(
            new P2PTradeRequest(P2PSide.Buy, Money.Parse("10.00", TestCurrencies.EIsla), "cup_tm"),
            Json);

        Assert.DoesNotContain("quotedRate", json, StringComparison.Ordinal);
    }
}
