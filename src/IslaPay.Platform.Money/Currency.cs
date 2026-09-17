namespace IslaPay.Platform;

/// <summary>
/// The currencies IslaPay holds. These mirror the client's <c>Currency</c>
/// enum exactly — see <c>API_CONTRACT.md</c> §2 in the Flutter repository.
/// </summary>
public enum Currency
{
    /// <summary>Internal account. Not on any chain.</summary>
    Usd,

    /// <summary>USD Coin. On-chain, so it carries a network.</summary>
    Usdc,

    /// <summary>Tether. On-chain, so it carries a network.</summary>
    Usdt,
}

public static class CurrencyExtensions
{
    /// <summary>
    /// Decimal places the currency is accounted in. This is the whole reason
    /// <see cref="Money"/> can be an integer: a balance is always a whole
    /// number of these units, never a fraction of one.
    /// </summary>
    public static int Scale(this Currency currency) => currency switch
    {
        Currency.Usd => 2,
        Currency.Usdc => 6,
        Currency.Usdt => 6,
        _ => throw new ArgumentOutOfRangeException(nameof(currency), currency, null),
    };

    /// <summary>The code used on the wire and in the UI.</summary>
    public static string Code(this Currency currency) => currency switch
    {
        Currency.Usd => "USD",
        Currency.Usdc => "USDC",
        Currency.Usdt => "USDT",
        _ => throw new ArgumentOutOfRangeException(nameof(currency), currency, null),
    };

    /// <summary>USD is an internal account; the stablecoins travel over a network.</summary>
    public static bool IsOnChain(this Currency currency) => currency != Currency.Usd;

    /// <summary>
    /// Parses a wire code. Case-insensitive, because a code that differs only
    /// in case is a client formatting quirk, not a different currency.
    /// </summary>
    public static bool TryParseCode(string? code, out Currency currency)
    {
        switch (code?.Trim().ToUpperInvariant())
        {
            case "USD": currency = Currency.Usd; return true;
            case "USDC": currency = Currency.Usdc; return true;
            case "USDT": currency = Currency.Usdt; return true;
            default: currency = default; return false;
        }
    }

    public static Currency ParseCode(string code) =>
        TryParseCode(code, out var currency)
            ? currency
            : throw new FormatException($"Unknown currency code '{code}'.");
}
