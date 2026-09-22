using IslaPay.TestSupport;
using System.Text.Json;
using IslaPay.Exchange.Contracts;
using IslaPay.Platform;
using IslaPay.Platform.Serialization;

namespace IslaPay.Exchange.Tests;

public class ExchangeWireTests
{
    private static readonly JsonSerializerOptions Json = IslaPayJson.Create(TestCurrencies.Scales);

    [Fact]
    public void An_unexecutable_quote_is_a_normal_response_with_a_reason()
    {
        var quote = new QuoteResponse(
            QuoteId: "01JQ",
            From: "EISLA",
            To: "USDT",
            Amount: Money.Parse("100.00", TestCurrencies.EIsla),
            Fee: Money.Parse("1.00", TestCurrencies.EIsla),
            Received: Money.Parse("99.00", TestCurrencies.Usdt),
            Rate: "1.0000",
            ExpiresAt: new DateTimeOffset(2026, 9, 17, 10, 0, 30, TimeSpan.Zero),
            Executable: false,
            Reason: ExchangeErrors.FundUnavailable);

        var json = JsonSerializer.Serialize(quote, Json);

        Assert.Contains("\"executable\":false", json, StringComparison.Ordinal);
        Assert.Contains("\"reason\":\"fund_unavailable\"", json, StringComparison.Ordinal);
        // The fee is in the source currency, what lands is in the target.
        Assert.Contains("\"fee\":{\"amount\":\"1.00\",\"currency\":\"EISLA\"}", json, StringComparison.Ordinal);
        Assert.Contains("\"received\":{\"amount\":\"99.000000\",\"currency\":\"USDT\"}", json, StringComparison.Ordinal);
    }

    [Fact]
    public void An_executable_quote_omits_the_reason()
    {
        var quote = new QuoteResponse(
            "01JQ", "EISLA", "USDT",
            Money.Parse("100.00", TestCurrencies.EIsla),
            Money.Parse("1.00", TestCurrencies.EIsla),
            Money.Parse("99.00", TestCurrencies.Usdt),
            "1.0000",
            new DateTimeOffset(2026, 9, 17, 10, 0, 30, TimeSpan.Zero),
            Executable: true);

        Assert.DoesNotContain("reason", JsonSerializer.Serialize(quote, Json), StringComparison.Ordinal);
    }

    [Fact]
    public void Executing_sends_only_the_quote_id()
    {
        // No amounts: the client cannot execute at figures the user never saw.
        Assert.Equal(
            """{"quoteId":"01JQ"}""",
            JsonSerializer.Serialize(new ConversionRequest("01JQ"), Json));
    }

    [Fact]
    public void The_fund_code_needs_a_currency_in_meta()
    {
        // ExchangeFundUnavailable(currency) on the client side cannot be
        // constructed without one.
        Assert.Contains(ExchangeErrors.FundUnavailable, ExchangeErrors.RequireCurrencyMeta);
        Assert.DoesNotContain(ExchangeErrors.QuoteExpired, ExchangeErrors.RequireCurrencyMeta);
    }
}
