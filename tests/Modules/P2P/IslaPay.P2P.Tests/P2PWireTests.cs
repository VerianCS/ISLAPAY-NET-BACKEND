using System.Text.Json;
using IslaPay.P2P.Contracts;
using IslaPay.Platform;
using IslaPay.Platform.Serialization;

namespace IslaPay.P2P.Tests;

public class P2PWireTests
{
    private static readonly JsonSerializerOptions Json = IslaPayJson.Options;

    [Fact]
    public void Side_travels_as_a_name_not_an_ordinal()
    {
        var json = JsonSerializer.Serialize(
            new P2PTradeRequest(P2PSide.Sell, Money.Parse("100.00", Currency.Usdt), "cup_tm"),
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
    public void A_method_defaults_to_available()
    {
        var method = new P2PMethodDto("cup_tm", "CUP Transfermóvil", "CUP", "380");
        Assert.True(method.Available);
        Assert.Contains("\"available\":true", JsonSerializer.Serialize(method, Json), StringComparison.Ordinal);
    }
}
