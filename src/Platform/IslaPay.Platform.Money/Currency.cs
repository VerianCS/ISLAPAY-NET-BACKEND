namespace IslaPay.Platform;

/// <summary>
/// The currencies IslaPay holds.
/// </summary>
/// <remarks>
/// The first three mirror the client's <c>Currency</c> enum — see
/// <c>API_CONTRACT.md</c> §2 in the Flutter repository — and are the ones a
/// customer can hold a balance in.
/// <para>
/// <see cref="Cup"/> is not one of them. No user account is ever opened in it:
/// it is the platform's own currency for the local leg of a P2P trade, held by
/// the settlement fund and owed through escrow until an operator pays it out.
/// A customer sees an amount in CUP; they never have a balance in it, which is
/// why it is absent from <c>WalletService.OpenedOnRegistration</c>.
/// </para>
/// </remarks>
public enum Currency
{
    /// <summary>Internal account. Not on any chain.</summary>
    Usd,

    /// <summary>USD Coin. On-chain, so it carries a network.</summary>
    Usdc,

    /// <summary>Tether. On-chain, so it carries a network.</summary>
    Usdt,

    /// <summary>
    /// Cuban peso. The platform's only, for the off-platform leg of a trade.
    /// </summary>
    Cup,
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
        Currency.Cup => 2,
        _ => throw new ArgumentOutOfRangeException(nameof(currency), currency, null),
    };

    /// <summary>The code used on the wire and in the UI.</summary>
    public static string Code(this Currency currency) => currency switch
    {
        Currency.Usd => "USD",
        Currency.Usdc => "USDC",
        Currency.Usdt => "USDT",
        Currency.Cup => "CUP",
        _ => throw new ArgumentOutOfRangeException(nameof(currency), currency, null),
    };

    /// <summary>
    /// Whether the currency settles on a chain, and therefore carries a network.
    /// </summary>
    /// <remarks>
    /// Named rather than negated. This used to read <c>!= Usd</c>, which was
    /// true of everything that was not USD and became wrong the moment a
    /// second off-chain currency existed — CUP moves through a Cuban bank, not
    /// a blockchain.
    /// </remarks>
    public static bool IsOnChain(this Currency currency) =>
        currency is Currency.Usdc or Currency.Usdt;

    /// <summary>
    /// Whether a customer may hold a balance in it.
    /// </summary>
    /// <remarks>
    /// CUP may not: see the remarks on <see cref="Currency"/>. Asked here so
    /// that a module cannot open a user account in it by accident.
    /// </remarks>
    public static bool IsCustomerHoldable(this Currency currency) =>
        currency is not Currency.Cup;

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
            case "CUP": currency = Currency.Cup; return true;
            default: currency = default; return false;
        }
    }

    public static Currency ParseCode(string code) =>
        TryParseCode(code, out var currency)
            ? currency
            : throw new FormatException($"Unknown currency code '{code}'.");
}
