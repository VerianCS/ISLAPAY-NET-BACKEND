using System.Globalization;

namespace IslaPay.Platform;

/// <summary>
/// An amount of money, held as a whole number of the currency's minor units.
/// </summary>
/// <remarks>
/// <para>
/// Money is never a <see cref="double"/> here, and never becomes one in
/// transit: a binary float cannot represent 0.01, so arithmetic on it drifts
/// and two systems that both "have" the same balance can disagree by a cent.
/// Every value is an integer of minor units (see
/// <see cref="CurrencyExtensions.Scale"/>), so addition and subtraction are
/// exact and only division has to make a decision — which this type always
/// makes explicitly.
/// </para>
/// <para>
/// On the wire the amount is a decimal <em>string</em>, per the API contract:
/// <c>{"amount": "100.50", "currency": "USD"}</c>. Parsing goes straight from
/// that string to minor units without passing through a float.
/// </para>
/// </remarks>
public readonly record struct Money : IComparable<Money>
{
    private Money(long minorUnits, Currency currency)
    {
        MinorUnits = minorUnits;
        Currency = currency;
    }

    /// <summary>Whole number of minor units. Negative means money out.</summary>
    public long MinorUnits { get; }

    public Currency Currency { get; }

    public static Money Zero(Currency currency) => new(0, currency);

    public static Money FromMinorUnits(long minorUnits, Currency currency) =>
        new(minorUnits, currency);

    /// <summary>
    /// For test data and for values that are already exact decimals. Rejects a
    /// value with more precision than the currency has, rather than rounding
    /// it silently — losing precision quietly is how ledgers stop balancing.
    /// </summary>
    public static Money FromDecimal(decimal value, Currency currency)
    {
        var factor = Pow10(currency.Scale());
        var scaled = value * factor;
        if (scaled != decimal.Truncate(scaled))
        {
            throw new ArgumentException(
                $"{value} has more precision than {currency.Code()} holds " +
                $"({currency.Scale()} decimal places).",
                nameof(value));
        }

        return new Money((long)scaled, currency);
    }

    // ---------------------------------------------------------------- parsing

    /// <summary>
    /// Parses the wire form: a decimal string plus a currency code.
    /// </summary>
    public static Money Parse(string amount, string currencyCode) =>
        TryParse(amount, currencyCode, out var money)
            ? money
            : throw new FormatException(
                $"'{amount}' {currencyCode} is not a valid amount.");

    public static Money Parse(string amount, Currency currency) =>
        TryParse(amount, currency, out var money)
            ? money
            : throw new FormatException(
                $"'{amount}' {currency.Code()} is not a valid amount.");

    public static bool TryParse(string? amount, string? currencyCode, out Money money)
    {
        money = default;
        return CurrencyExtensions.TryParseCode(currencyCode, out var currency)
               && TryParse(amount, currency, out money);
    }

    /// <summary>
    /// Parses a decimal string exactly. Accepts an optional sign, digits, and
    /// at most as many decimal places as the currency holds.
    /// </summary>
    /// <remarks>
    /// Deliberately strict. It rejects thousands separators, exponent
    /// notation, whitespace inside the number and excess precision, because
    /// every one of those means the caller and this type disagree about what
    /// the value is — and for money that disagreement should surface at the
    /// edge, not deep in a ledger posting.
    /// </remarks>
    public static bool TryParse(string? amount, Currency currency, out Money money)
    {
        money = default;
        if (string.IsNullOrEmpty(amount)) return false;

        var span = amount.AsSpan();
        var negative = false;
        var i = 0;

        if (span[0] is '-' or '+')
        {
            negative = span[0] == '-';
            i = 1;
        }

        if (i >= span.Length) return false;

        long units = 0;
        var digits = 0;
        for (; i < span.Length && span[i] != '.'; i++)
        {
            if (!char.IsAsciiDigit(span[i])) return false;
            try
            {
                units = checked(units * 10 + (span[i] - '0'));
            }
            catch (OverflowException)
            {
                return false;
            }

            digits++;
        }

        if (digits == 0) return false;

        var scale = currency.Scale();
        var fractionDigits = 0;

        if (i < span.Length)
        {
            i++; // consume '.'
            if (i >= span.Length) return false; // trailing '.' is malformed

            for (; i < span.Length; i++)
            {
                if (!char.IsAsciiDigit(span[i])) return false;
                fractionDigits++;
                // Excess precision is a mismatch, not something to round away.
                if (fractionDigits > scale) return false;
                try
                {
                    units = checked(units * 10 + (span[i] - '0'));
                }
                catch (OverflowException)
                {
                    return false;
                }
            }
        }

        // Pad the fraction out to the currency's full scale.
        for (var pad = fractionDigits; pad < scale; pad++)
        {
            try
            {
                units = checked(units * 10);
            }
            catch (OverflowException)
            {
                return false;
            }
        }

        money = new Money(negative ? -units : units, currency);
        return true;
    }

    // --------------------------------------------------------------- rendering

    /// <summary>
    /// The wire form: a plain decimal string with exactly the currency's
    /// number of decimal places, no separators, no exponent.
    /// </summary>
    public override string ToString()
    {
        var scale = Currency.Scale();
        var negative = MinorUnits < 0;
        // Negating in ulong space so long.MinValue does not overflow.
        var magnitude = negative ? (ulong)(-(MinorUnits + 1)) + 1 : (ulong)MinorUnits;

        var factor = (ulong)Pow10(scale);
        var whole = magnitude / factor;
        var fraction = magnitude % factor;

        var sign = negative ? "-" : string.Empty;
        return scale == 0
            ? $"{sign}{whole}"
            : $"{sign}{whole}.{fraction.ToString(CultureInfo.InvariantCulture).PadLeft(scale, '0')}";
    }

    /// <summary>Major units as a decimal. For reporting, never for arithmetic.</summary>
    public decimal ToDecimal() => (decimal)MinorUnits / Pow10(Currency.Scale());

    // -------------------------------------------------------------- arithmetic

    public static Money operator +(Money a, Money b)
    {
        Guard(a, b);
        return new Money(checked(a.MinorUnits + b.MinorUnits), a.Currency);
    }

    public static Money operator -(Money a, Money b)
    {
        Guard(a, b);
        return new Money(checked(a.MinorUnits - b.MinorUnits), a.Currency);
    }

    public static Money operator -(Money value) =>
        new(checked(-value.MinorUnits), value.Currency);

    public static bool operator >(Money a, Money b) { Guard(a, b); return a.MinorUnits > b.MinorUnits; }
    public static bool operator <(Money a, Money b) { Guard(a, b); return a.MinorUnits < b.MinorUnits; }
    public static bool operator >=(Money a, Money b) { Guard(a, b); return a.MinorUnits >= b.MinorUnits; }
    public static bool operator <=(Money a, Money b) { Guard(a, b); return a.MinorUnits <= b.MinorUnits; }

    public int CompareTo(Money other)
    {
        Guard(this, other);
        return MinorUnits.CompareTo(other.MinorUnits);
    }

    public bool IsZero => MinorUnits == 0;
    public bool IsPositive => MinorUnits > 0;
    public bool IsNegative => MinorUnits < 0;
    public Money Abs() => MinorUnits < 0 ? -this : this;

    /// <summary>
    /// A fee or share expressed in basis points (1 bp = 0.01%), so a 1% fee is
    /// <c>100</c> and the arithmetic stays integral.
    /// </summary>
    /// <param name="basisPoints">Hundredths of a percent. 100 = 1%.</param>
    /// <param name="rounding">
    /// How to settle a fraction of a minor unit. This is a policy decision —
    /// it decides who keeps the sub-cent — so the caller states it rather than
    /// inheriting a default from somewhere else.
    /// </param>
    public Money MultiplyByBasisPoints(int basisPoints, MidpointRounding rounding)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(basisPoints);
        var result = DivideRounded((Int128)MinorUnits * basisPoints, 10_000, rounding);
        return new Money((long)result, Currency);
    }

    /// <summary>
    /// Converts to another currency at <paramref name="rate"/> units of
    /// <paramref name="target"/> per one unit of this currency, changing scale
    /// as it goes.
    /// </summary>
    public Money ConvertTo(Currency target, decimal rate, MidpointRounding rounding)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(rate);
        if (target == Currency) return this;

        // decimal is base-10, so this scaling is exact for any amount the
        // ledger can hold; the only rounding is the deliberate one below.
        var major = (decimal)MinorUnits / Pow10(Currency.Scale());
        var converted = major * rate * Pow10(target.Scale());
        return new Money((long)Math.Round(converted, 0, rounding), target);
    }

    // ------------------------------------------------------------------ guards

    private static void Guard(Money a, Money b)
    {
        if (a.Currency != b.Currency)
        {
            throw new InvalidOperationException(
                $"Cannot combine {a.Currency.Code()} with {b.Currency.Code()}. " +
                "Convert explicitly through the exchange instead.");
        }
    }

    private static decimal Pow10(int exponent)
    {
        decimal result = 1;
        for (var i = 0; i < exponent; i++) result *= 10;
        return result;
    }

    /// <summary>
    /// Integer division that rounds instead of truncating. Exact: it never
    /// leaves integer space, so there is no float error to inherit.
    /// </summary>
    private static Int128 DivideRounded(Int128 numerator, Int128 denominator, MidpointRounding rounding)
    {
        var quotient = numerator / denominator;
        var remainder = numerator - quotient * denominator;
        if (remainder == 0) return quotient;

        var negative = (numerator < 0) ^ (denominator < 0);
        var twiceRemainder = Int128.Abs(remainder) * 2;
        var absDenominator = Int128.Abs(denominator);
        var step = negative ? -1 : 1;

        var roundAway = rounding switch
        {
            MidpointRounding.ToZero => false,
            MidpointRounding.AwayFromZero => twiceRemainder >= absDenominator,
            MidpointRounding.ToEven => twiceRemainder > absDenominator
                                       || (twiceRemainder == absDenominator && !Int128.IsEvenInteger(quotient)),
            MidpointRounding.ToPositiveInfinity => !negative,
            MidpointRounding.ToNegativeInfinity => negative,
            _ => throw new ArgumentOutOfRangeException(nameof(rounding), rounding, null),
        };

        return roundAway ? quotient + step : quotient;
    }
}
