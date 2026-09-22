using System.Text.Json;
using IslaPay.Platform;

namespace IslaPay.Platform.Tests;

/// <summary>
/// The two currencies these tests need, stated outright.
/// </summary>
/// <remarks>
/// A platform test must not know that a catalogue exists — <c>Money</c> does
/// not, and that is the point of it carrying its own scale. Two decimal places
/// and six are picked because they are the pair where a scale mistake is
/// visible: the same minor units mean a dollar in one and a millionth of one
/// in the other.
/// </remarks>
internal static class Test
{
    public static readonly Currency Eisla = Currency.Of("EISLA", 2);

    public static readonly Currency Usdt = Currency.Of("USDT", 6);

    /// <summary>Turns a scale from an <c>InlineData</c> row back into a currency.</summary>
    /// <remarks>
    /// An attribute argument has to be a compile-time constant, and a
    /// <see cref="Currency"/> cannot be one now that it is not an enum. The
    /// scale is what each row is actually varying, so that is what the rows
    /// carry.
    /// </remarks>
    public static Currency OfScale(int scale) => scale == 2 ? Eisla : Usdt;
}

public class ParsingTests
{
    [Theory]
    [InlineData("0", 2, 0L)]
    [InlineData("1", 2, 100L)]
    [InlineData("100.50", 2, 10_050L)]
    [InlineData("100.5", 2, 10_050L)]
    [InlineData("-42.07", 2, -4_207L)]
    [InlineData("+3.00", 2, 300L)]
    [InlineData("0.01", 2, 1L)]
    [InlineData("1", 6, 1_000_000L)]
    [InlineData("0.000001", 6, 1L)]
    [InlineData("1234.567890", 6, 1_234_567_890L)]
    public void Parses_exact_decimal_strings(string input, int scale, long expected)
    {
        Assert.Equal(expected, Money.Parse(input, Test.OfScale(scale)).MinorUnits);
    }

    [Theory]
    // Excess precision is a disagreement about the value, not something to round.
    [InlineData("0.001", 2)]
    [InlineData("1.005", 2)]
    [InlineData("0.0000001", 6)]
    // Shapes that mean the caller and this type disagree about the format.
    [InlineData("1,000.00", 2)]
    [InlineData("1e3", 2)]
    [InlineData("1 000", 2)]
    [InlineData(" 1.00", 2)]
    [InlineData("1.00 ", 2)]
    [InlineData("1.", 2)]
    [InlineData(".5", 2)]
    [InlineData("", 2)]
    [InlineData("-", 2)]
    [InlineData("abc", 2)]
    [InlineData("$1.00", 2)]
    public void Rejects_anything_ambiguous(string input, int scale)
    {
        Assert.False(Money.TryParse(input, Test.OfScale(scale), out _));
    }

    [Theory]
    [InlineData("eisla")]
    [InlineData("EISLA")]
    [InlineData(" EisLa ")]
    public void A_code_is_trimmed_and_upper_cased(string code)
    {
        Assert.Equal("EISLA", Currency.Of(code, 2).Code);
    }

    /// <summary>
    /// A currency is a code <i>and</i> a scale, and the scale is not optional.
    /// </summary>
    /// <remarks>
    /// The whole reason this type stopped being an enum, seen from the inside:
    /// nothing here can name a currency without also saying how it is
    /// accounted, so nothing here can be the second, disagreeing catalogue.
    /// </remarks>
    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("E")]
    [InlineData("TOOLONGACURRENCY")]
    [InlineData("eu ro")]
    [InlineData("US$")]
    public void Refuses_a_code_that_is_not_one(string code)
    {
        Assert.ThrowsAny<ArgumentException>(() => Currency.Of(code, 2));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(19)]
    public void Refuses_a_scale_no_integer_can_hold(int scale)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Currency.Of("EISLA", scale));
    }

    /// <summary>
    /// <c>default(Currency)</c> is not a currency, and does not pretend to be.
    /// </summary>
    /// <remarks>
    /// A struct always has a zero value. If this one behaved as "zero of
    /// something" it would balance a posting it had nothing to do with, which
    /// is the one failure a double-entry ledger cannot detect for itself.
    /// </remarks>
    [Fact]
    public void The_default_currency_is_refused_rather_than_guessed()
    {
        var none = default(Currency);

        Assert.False(none.IsDefined);
        Assert.Equal("(none)", none.ToString());
        Assert.False(Money.TryParse("1.00", none, out _));
    }

    /// <summary>
    /// The internal unit used to be called USD, and nothing here maps the old
    /// code onto E-ISLA.
    /// </summary>
    /// <remarks>
    /// Accepting it would make a client built against the old contract keep
    /// working while telling people they hold US dollars, which IslaPay does
    /// not issue. The refusal now lives in the catalogue — there is no
    /// <c>USD</c> row — and this asserts the half of it that is here: a scale
    /// lookup answers for what it was told about and nothing else.
    /// </remarks>
    [Theory]
    [InlineData("USD")]
    [InlineData("usd")]
    public void An_unlisted_code_has_no_scale_to_read_it_with(string code)
    {
        var scales = new StatedScales(Test.Eisla, Test.Usdt);

        Assert.False(scales.TryGetScale(code, out _));
        Assert.False(Money.TryParse("1.00", code, scales, out _));
    }

    [Fact]
    public void Stated_scales_read_a_code_however_it_is_cased()
    {
        var scales = new StatedScales(Test.Eisla, Test.Usdt);

        Assert.True(Money.TryParse("1.5", " usdt ", scales, out var money));
        Assert.Equal(1_500_000L, money.MinorUnits);
        Assert.Equal("USDT", money.Currency.Code);
    }

    [Theory]
    [InlineData("0", 2, "0.00")]
    [InlineData("100.5", 2, "100.50")]
    [InlineData("-42.07", 2, "-42.07")]
    [InlineData("1", 6, "1.000000")]
    [InlineData("-0.000001", 6, "-0.000001")]
    public void Renders_at_the_currencys_full_scale(string input, int scale, string expected)
    {
        Assert.Equal(expected, Money.Parse(input, Test.OfScale(scale)).ToString());
    }

    [Theory]
    [InlineData("0.00", 2)]
    [InlineData("100.50", 2)]
    [InlineData("-42.07", 2)]
    [InlineData("999999.999999", 6)]
    public void Round_trips_through_its_own_string(string input, int scale)
    {
        var currency = Test.OfScale(scale);
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
        var sum = Money.Parse("0.10", Test.Eisla) + Money.Parse("0.20", Test.Eisla);
        Assert.Equal("0.30", sum.ToString());

        // A hundred cents is exactly one dollar, however you get there.
        var total = Money.Zero(Test.Eisla);
        for (var i = 0; i < 100; i++) total += Money.Parse("0.01", Test.Eisla);
        Assert.Equal("1.00", total.ToString());
    }

    [Fact]
    public void Refuses_to_mix_currencies()
    {
        var usd = Money.Parse("1.00", Test.Eisla);
        var usdt = Money.Parse("1.00", Test.Usdt);

        Assert.Throws<InvalidOperationException>(() => usd + usdt);
        Assert.Throws<InvalidOperationException>(() => usd - usdt);
        Assert.Throws<InvalidOperationException>(() => usd > usdt);
    }

    [Fact]
    public void Negation_and_sign_behave()
    {
        var value = Money.Parse("-12.34", Test.Eisla);
        Assert.True(value.IsNegative);
        Assert.Equal("12.34", value.Abs().ToString());
        Assert.Equal("12.34", (-value).ToString());
        Assert.True(Money.Zero(Test.Eisla).IsZero);
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
        var fee = Money.Parse(amount, Test.Eisla)
            .MultiplyByBasisPoints(bps, MidpointRounding.ToEven);
        Assert.Equal(expected, fee.ToString());
    }

    [Fact]
    public void Rounding_mode_decides_who_keeps_the_sub_cent()
    {
        // 1% of 33.33 is 0.3333 — a third of a cent has to go somewhere.
        var amount = Money.Parse("33.33", Test.Eisla);

        Assert.Equal("0.33", amount.MultiplyByBasisPoints(100, MidpointRounding.ToZero).ToString());
        Assert.Equal("0.34", amount.MultiplyByBasisPoints(100, MidpointRounding.ToPositiveInfinity).ToString());
        Assert.Equal("0.33", amount.MultiplyByBasisPoints(100, MidpointRounding.ToEven).ToString());
    }

    [Fact]
    public void Exact_midpoint_splits_by_mode()
    {
        // 0.005 lands exactly halfway between two cents.
        var amount = Money.Parse("0.50", Test.Eisla);

        Assert.Equal("0.01", amount.MultiplyByBasisPoints(100, MidpointRounding.AwayFromZero).ToString());
        Assert.Equal("0.00", amount.MultiplyByBasisPoints(100, MidpointRounding.ToEven).ToString());
        Assert.Equal("0.00", amount.MultiplyByBasisPoints(100, MidpointRounding.ToZero).ToString());
    }

    [Fact]
    public void A_fee_never_exceeds_the_amount_it_is_taken_from()
    {
        foreach (var cents in new[] { 1, 7, 99, 100, 4_567, 1_000_000 })
        {
            var amount = Money.FromMinorUnits(cents, Test.Eisla);
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
        var usd = Money.Parse("100.00", Test.Eisla);
        var usdt = usd.ConvertTo(Test.Usdt, 1.0m, MidpointRounding.ToEven);

        Assert.Equal(Test.Usdt, usdt.Currency);
        Assert.Equal("100.000000", usdt.ToString());
    }

    [Fact]
    public void Converting_to_the_same_currency_is_a_no_op()
    {
        var usd = Money.Parse("12.34", Test.Eisla);
        Assert.Equal(usd, usd.ConvertTo(Test.Eisla, 999m, MidpointRounding.ToEven));
    }

    [Fact]
    public void Full_conversion_matches_the_contract_example()
    {
        // API_CONTRACT.md §3: 100.00 E-ISLA at 1% fee → fee 1.00, received 99.00.
        var amount = Money.Parse("100.00", Test.Eisla);
        var fee = amount.MultiplyByBasisPoints(100, MidpointRounding.ToEven);
        var received = (amount - fee).ConvertTo(Test.Usdt, 1.0m, MidpointRounding.ToEven);

        Assert.Equal("1.00", fee.ToString());
        Assert.Equal("99.000000", received.ToString());
    }
}

public class JsonTests
{
    /// <summary>
    /// Options that can read as well as write.
    /// </summary>
    /// <remarks>
    /// Writing money needs nothing — a <see cref="Money"/> carries its own
    /// scale. Reading one gets a code and a decimal string, and has to be told
    /// somewhere what <c>"1.5"</c> in USDT means. A converter built without
    /// that refuses every read rather than guessing, which is what the last
    /// test here asserts.
    /// </remarks>
    private static readonly JsonSerializerOptions Options = new()
    {
        Converters = { new MoneyJsonConverter(new StatedScales(Test.Eisla, Test.Usdt)) },
    };

    [Fact]
    public void Writes_the_contract_shape()
    {
        var json = JsonSerializer.Serialize(Money.Parse("1250.00", Test.Eisla), Options);
        Assert.Equal("""{"amount":"1250.00","currency":"EISLA"}""", json);
    }

    [Fact]
    public void Reads_the_contract_shape()
    {
        var money = JsonSerializer.Deserialize<Money>(
            """{"amount":"-350.000000","currency":"USDT"}""", Options);

        Assert.Equal(Test.Usdt, money.Currency);
        Assert.Equal("-350.000000", money.ToString());
        Assert.True(money.IsNegative);
    }

    [Fact]
    public void Rejects_a_numeric_amount_loudly()
    {
        // An unmigrated client sending 100.50 instead of "100.50" must fail
        // at the edge, not quietly become a float somewhere downstream.
        var ex = Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<Money>(
            """{"amount":100.50,"currency":"EISLA"}""", Options));

        Assert.Contains("must be a string", ex.Message);
    }

    /// <summary>
    /// A converter with no scales refuses a read, and says how to fix it.
    /// </summary>
    /// <remarks>
    /// The alternative was a default scale, which would be right for the
    /// currencies that happen to have it and wrong by a factor of ten thousand
    /// for the rest — silently, in a balance.
    /// </remarks>
    [Fact]
    public void Reading_without_a_catalogue_fails_rather_than_guessing()
    {
        var blind = new JsonSerializerOptions { Converters = { new MoneyJsonConverter() } };

        var ex = Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<Money>(
            """{"amount":"1.50","currency":"USDT"}""", blind));

        Assert.Contains("USDT", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Survives_a_round_trip()
    {
        var original = Money.Parse("999999.999999", Test.Usdt);
        var json = JsonSerializer.Serialize(original, Options);
        Assert.Equal(original, JsonSerializer.Deserialize<Money>(json, Options));
    }
}
