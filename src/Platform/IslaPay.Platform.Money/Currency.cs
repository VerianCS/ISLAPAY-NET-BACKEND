namespace IslaPay.Platform;

/// <summary>
/// The currencies IslaPay holds.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="EIsla"/> is the application's own unit and the one a balance is
/// usually quoted in. It is not a token and it is not on a chain: it is a
/// liability of IslaPay, redeemable one for one against a stablecoin subject
/// to the settlement fund having it. It is only ever issued by converting a
/// deposit, by a P2P purchase, or by a treasury credit an operator signs for —
/// never by anything a customer can call.
/// </para>
/// <para>
/// <see cref="Cup"/> is the odd one out. No user account is ever opened in it:
/// it is the platform's own currency for the local leg of a P2P trade, held by
/// the settlement fund and owed through escrow until an operator pays it out.
/// A customer sees an amount in CUP; they never have a balance in it, which is
/// why it is absent from <c>WalletService.OpenedOnRegistration</c>. It belongs
/// to P2P alone — the exchange and custody never touch it.
/// </para>
/// </remarks>
public enum Currency
{
    /// <summary>
    /// E-ISLA, the application's unit of account. Internal, not on any chain.
    /// </summary>
    EIsla,

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
    /// <remarks>
    /// E-ISLA is accounted in hundredths because it is redeemable one for one
    /// against a dollar-denominated stablecoin and a unit a customer cannot be
    /// shown is a unit that gets lost in rounding. The stablecoins keep their
    /// own six, which is what their contracts use; converting between the two
    /// scales is <see cref="Money.ConvertTo"/>'s problem, not this one's.
    /// </remarks>
    public static int Scale(this Currency currency) => currency switch
    {
        Currency.EIsla => 2,
        Currency.Usdc => 6,
        Currency.Usdt => 6,
        Currency.Cup => 2,
        _ => throw new ArgumentOutOfRangeException(nameof(currency), currency, null),
    };

    /// <summary>The code used on the wire and in the UI.</summary>
    public static string Code(this Currency currency) => currency switch
    {
        Currency.EIsla => "EISLA",
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
    /// true of everything that was not the internal unit and became wrong the
    /// moment a second off-chain currency existed — CUP moves through a Cuban
    /// bank, not a blockchain, and E-ISLA moves nowhere at all.
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
    /// <remarks>
    /// <c>USD</c> is deliberately not accepted. It was this enum's first member
    /// and it named the same internal unit E-ISLA names now, so taking it as an
    /// alias would look kind; it would also mean a client built against the old
    /// contract keeps working while showing people a currency IslaPay does not
    /// issue. A rejected code is a bug report. A silently accepted one is not.
    /// </remarks>
    public static bool TryParseCode(string? code, out Currency currency)
    {
        switch (code?.Trim().ToUpperInvariant())
        {
            case "EISLA": currency = Currency.EIsla; return true;
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
