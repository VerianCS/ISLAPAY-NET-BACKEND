using System.Text.Json;
using IslaPay.Platform;

namespace IslaPay.Platform.Tests;

public class ParsingTests
{
    [Theory]
    [InlineData("0", Currency.Usd, 0L)]
    [InlineData("1", Currency.Usd, 100L)]
    [InlineData("100.50", Currency.Usd, 10_050L)]
    [InlineData("100.5", Currency.Usd, 10_050L)]
    [InlineData("-42.07", Currency.Usd, -4_207L)]
    [InlineData("+3.00", Currency.Usd, 300L)]
    [InlineData("0.01", Currency.Usd, 1L)]
    [InlineData("1", Currency.Usdt, 1_000_000L)]
    [InlineData("0.000001", Currency.Usdt, 1L)]
    [InlineData("1234.567890", Currency.Usdt, 1_234_567_890L)]
    public void Parses_exact_decimal_strings(string input, Currency currency, long expected)
    {
        Assert.Equal(expected, Money.Parse(input, currency).MinorUnits);
    }

    [Theory]
    // Excess precision is a disagreement about the value, not something to round.
    [InlineData("0.001", Currency.Usd)]
    [InlineData("1.005", Currency.Usd)]
    [InlineData("0.0000001", Currency.Usdt)]
    // Shapes that mean the caller and this type disagree about the format.
    [InlineData("1,000.00", Currency.Usd)]
    [InlineData("1e3", Currency.Usd)]
    [InlineData("1 000", Currency.Usd)]
    [InlineData(" 1.00", Currency.Usd)]
    [InlineData("1.00 ", Currency.Usd)]
    [InlineData("1.", Currency.Usd)]
    [InlineData(".5", Currency.Usd)]
    [InlineData("", Currency.Usd)]
    [InlineData("-", Currency.Usd)]
    [InlineData("abc", Currency.Usd)]
    [InlineData("$1.00", Currency.Usd)]
    public void Rejects_anything_ambiguous(string input, Currency currency)
    {
        Assert.False(Money.TryParse(input, currency, out _));
    }

    [Theory]
    [InlineData("usd", Currency.Usd)]
    [InlineData("USD", Currency.Usd)]
    [InlineData(" UsDt ", Currency.Usdt)]
    public void Currency_codes_are_case_insensitive(string code, Currency expected)
    {
        Assert.True(CurrencyExtensions.TryParseCode(code, out var currency));
        Assert.Equal(expected, currency);
    }

    [Theory]
    [InlineData("0", Currency.Usd, "0.00")]
    [InlineData("100.5", Currency.Usd, "100.50")]
    [InlineData("-42.07", Currency.Usd, "-42.07")]
    [InlineData("1", Currency.Usdt, "1.000000")]
    [InlineData("-0.000001", Currency.Usdt, "-0.000001")]
    public void Renders_at_the_currencys_full_scale(string input, Currency currency, string expected)
    {
        Assert.Equal(expected, Money.Parse(input, currency).ToString());
    }

    [Theory]
    [InlineData("0.00", Currency.Usd)]
    [InlineData("100.50", Currency.Usd)]
    [InlineData("-42.07", Currency.Usd)]
    [InlineData("999999.999999", Currency.Usdt)]
    public void Round_trips_through_its_own_string(string input, Currency currency)
    {
        var once = Money.Parse(input, currency);
        var twice = Money.Parse(once.ToString(), currency);
        Assert.Equal(once, twice);
    }
}

public class ArithmeticTests
{
    [Fact]
    public void Adds_and_subtracts_exactly_where_a_double_would_drift()
    {
        // The canonical float failure: 0.1 + 0.2 != 0.3 in binary.
        var sum = Money.Parse("0.10", Currency.Usd) + Money.Parse("0.20", Currency.Usd);
        Assert.Equal("0.30", sum.ToString());

        // A hundred cents is exactly one dollar, however you get there.
        var total = Money.Zero(Currency.Usd);
        for (var i = 0; i < 100; i++) total += Money.Parse("0.01", Currency.Usd);
        Assert.Equal("1.00", total.ToString());
    }

    [Fact]
    public void Refuses_to_mix_currencies()
    {
        var usd = Money.Parse("1.00", Currency.Usd);
        var usdt = Money.Parse("1.00", Currency.Usdt);

        Assert.Throws<InvalidOperationException>(() => usd + usdt);
        Assert.Throws<InvalidOperationException>(() => usd - usdt);
        Assert.Throws<InvalidOperationException>(() => usd > usdt);
    }

    [Fact]
    public void Negation_and_sign_behave()
    {
        var value = Money.Parse("-12.34", Currency.Usd);
        Assert.True(value.IsNegative);
        Assert.Equal("12.34", value.Abs().ToString());
        Assert.Equal("12.34", (-value).ToString());
        Assert.True(Money.Zero(Currency.Usd).IsZero);
    }
}

public class FeeTests
{
    [Theory]
    // 1% — the fee both conversions and P2P charge.
    [InlineData("100.00", 100, "1.00")]
    [InlineData("0.99", 100, "0.01")]
    [InlineData("1250.00", 100, "12.50")]
    public void One_percent_of_round_amounts(string amount, int bps, string expected)
    {
        var fee = Money.Parse(amount, Currency.Usd)
            .MultiplyByBasisPoints(bps, MidpointRounding.ToEven);
        Assert.Equal(expected, fee.ToString());
    }

    [Fact]
    public void Rounding_mode_decides_who_keeps_the_sub_cent()
    {
        // 1% of 33.33 is 0.3333 — a third of a cent has to go somewhere.
        var amount = Money.Parse("33.33", Currency.Usd);

        Assert.Equal("0.33", amount.MultiplyByBasisPoints(100, MidpointRounding.ToZero).ToString());
        Assert.Equal("0.34", amount.MultiplyByBasisPoints(100, MidpointRounding.ToPositiveInfinity).ToString());
        Assert.Equal("0.33", amount.MultiplyByBasisPoints(100, MidpointRounding.ToEven).ToString());
    }

    [Fact]
    public void Exact_midpoint_splits_by_mode()
    {
        // 0.005 lands exactly halfway between two cents.
        var amount = Money.Parse("0.50", Currency.Usd);

        Assert.Equal("0.01", amount.MultiplyByBasisPoints(100, MidpointRounding.AwayFromZero).ToString());
        Assert.Equal("0.00", amount.MultiplyByBasisPoints(100, MidpointRounding.ToEven).ToString());
        Assert.Equal("0.00", amount.MultiplyByBasisPoints(100, MidpointRounding.ToZero).ToString());
    }

    [Fact]
    public void A_fee_never_exceeds_the_amount_it_is_taken_from()
    {
        foreach (var cents in new[] { 1, 7, 99, 100, 4_567, 1_000_000 })
        {
            var amount = Money.FromMinorUnits(cents, Currency.Usd);
            var fee = amount.MultiplyByBasisPoints(100, MidpointRounding.AwayFromZero);
            Assert.True(fee <= amount, $"fee {fee} exceeded amount {amount}");
            Assert.False(fee.IsNegative);
        }
    }
}

public class ConversionTests
{
    [Fact]
    public void Changes_scale_between_currencies()
    {
        // USD holds 2 decimals, USDT holds 6.
        var usd = Money.Parse("100.00", Currency.Usd);
        var usdt = usd.ConvertTo(Currency.Usdt, 1.0m, MidpointRounding.ToEven);

        Assert.Equal(Currency.Usdt, usdt.Currency);
        Assert.Equal("100.000000", usdt.ToString());
    }

    [Fact]
    public void Converting_to_the_same_currency_is_a_no_op()
    {
        var usd = Money.Parse("12.34", Currency.Usd);
        Assert.Equal(usd, usd.ConvertTo(Currency.Usd, 999m, MidpointRounding.ToEven));
    }

    [Fact]
    public void Full_conversion_matches_the_contract_example()
    {
        // API_CONTRACT.md §3: 100.00 USD at 1% fee → fee 1.00, received 99.00.
        var amount = Money.Parse("100.00", Currency.Usd);
        var fee = amount.MultiplyByBasisPoints(100, MidpointRounding.ToEven);
        var received = (amount - fee).ConvertTo(Currency.Usdt, 1.0m, MidpointRounding.ToEven);

        Assert.Equal("1.00", fee.ToString());
        Assert.Equal("99.000000", received.ToString());
    }
}

public class JsonTests
{
    private static readonly JsonSerializerOptions Options =
        new() { Converters = { new MoneyJsonConverter() } };

    [Fact]
    public void Writes_the_contract_shape()
    {
        var json = JsonSerializer.Serialize(Money.Parse("1250.00", Currency.Usd), Options);
        Assert.Equal("""{"amount":"1250.00","currency":"USD"}""", json);
    }

    [Fact]
    public void Reads_the_contract_shape()
    {
        var money = JsonSerializer.Deserialize<Money>(
            """{"amount":"-350.000000","currency":"USDT"}""", Options);

        Assert.Equal(Currency.Usdt, money.Currency);
        Assert.Equal("-350.000000", money.ToString());
        Assert.True(money.IsNegative);
    }

    [Fact]
    public void Rejects_a_numeric_amount_loudly()
    {
        // An unmigrated client sending 100.50 instead of "100.50" must fail
        // at the edge, not quietly become a float somewhere downstream.
        var ex = Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<Money>(
            """{"amount":100.50,"currency":"USD"}""", Options));

        Assert.Contains("must be a string", ex.Message);
    }

    [Fact]
    public void Survives_a_round_trip()
    {
        var original = Money.Parse("999999.999999", Currency.Usdt);
        var json = JsonSerializer.Serialize(original, Options);
        Assert.Equal(original, JsonSerializer.Deserialize<Money>(json, Options));
    }
}
