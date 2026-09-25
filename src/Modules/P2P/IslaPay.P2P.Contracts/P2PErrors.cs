namespace IslaPay.P2P.Contracts;

/// <summary>Error codes the P2P module raises.</summary>
/// <remarks>
/// <see cref="InsufficientFunds"/> and <see cref="PhoneNotVerified"/> are
/// deliberately the same strings Wallet and Marketplace send: they are one
/// condition as far as the person holding the phone is concerned, and the
/// client models each as a single failure type.
/// </remarks>
public static class P2PErrors
{
    /// <summary>No rail with that id.</summary>
    public const string MethodNotFound = "method_not_found";

    /// <summary>
    /// The rail exists and is switched off. Distinct from not existing,
    /// because the client keeps it in the list greyed rather than hiding it.
    /// </summary>
    public const string MethodUnavailable = "method_unavailable";

    /// <summary>Nobody has published a rate for that rail, side and currency.</summary>
    public const string RateUnavailable = "rate_unavailable";

    /// <summary>
    /// The rate moved between the quote and the trade. Re-quote and retry —
    /// the client shows the new number and asks again rather than filling at
    /// a price the user never saw.
    /// </summary>
    public const string QuoteExpired = "quote_expired";

    /// <summary>Zero, negative, or in a currency the rail does not take.</summary>
    public const string InvalidAmount = "invalid_amount";

    /// <summary>Below the rail's minimum. <c>meta.minimum</c> carries it.</summary>
    public const string BelowMinimum = "amount_below_minimum";

    /// <summary>Above the rail's maximum. <c>meta.maximum</c> carries it.</summary>
    public const string AboveMaximum = "amount_above_maximum";

    /// <summary>
    /// The user's balance does not cover the sale. Client:
    /// <c>InsufficientFunds(currency)</c>, so <c>meta.currency</c> is required.
    /// </summary>
    public const string InsufficientFunds = "insufficient_funds";

    /// <summary>
    /// IslaPay cannot cover its side.
    /// </summary>
    /// <remarks>
    /// The settlement fund is a platform account and platform accounts may go
    /// negative, so nothing in the ledger would stop a sale IslaPay cannot pay
    /// for. This code is the thing that stops it, and a trade that reaches the
    /// ledger without this check having passed is a promise to send money that
    /// does not exist.
    /// </remarks>
    public const string FundUnavailable = "fund_unavailable";

    /// <summary>Money cannot move until the account's phone is proved (D11).</summary>
    public const string PhoneNotVerified = "phone_not_verified";

    /// <summary>No such trade, or not the caller's.</summary>
    public const string TradeNotFound = "trade_not_found";

    /// <summary>
    /// The trade is no longer waiting on anything: already paid, refunded,
    /// expired or called off.
    /// </summary>
    public const string TradeNotOpen = "trade_not_open";

    /// <summary>
    /// A settle or fail aimed at the wrong side — confirming a payout on a buy,
    /// or a receipt on a sell.
    /// </summary>
    public const string WrongSide = "wrong_side";

    /// <summary>A sell that does not say where to send the local money.</summary>
    public const string PayoutDestinationRequired = "payout_destination_required";

    /// <summary>A payout destination too long to be a card or a phone.</summary>
    public const string InvalidPayoutDestination = "invalid_payout_destination";

    /// <summary>A rail's payment instructions are longer than a person reads.</summary>
    public const string InvalidInstructions = "invalid_instructions";

    /// <summary>
    /// A currency that cannot stand where it was put: a wallet side that no
    /// customer holds, or a local side that is not a switched-on fiat
    /// currency.
    /// </summary>
    public const string InvalidCurrency = "invalid_currency";

    /// <summary>A rail's limits that are not a range: zero, negative or upside down.</summary>
    public const string InvalidLimits = "invalid_limits";

    /// <summary>A rail's name that is empty or longer than a screen shows.</summary>
    public const string InvalidMethodName = "invalid_method_name";

    /// <summary>
    /// There is already a rail for that currency. One currency, one rail: the
    /// peso is one market however many apps move it.
    /// </summary>
    public const string MethodExists = "method_exists";

    /// <summary>
    /// This module's codes whose <c>meta</c> must carry a <c>currency</c>,
    /// because the client's corresponding type cannot be constructed without
    /// one.
    /// </summary>
    public static IReadOnlySet<string> RequireCurrencyMeta { get; } =
        new HashSet<string>(StringComparer.Ordinal) { InsufficientFunds, FundUnavailable };
}
