using IslaPay.Platform;

namespace IslaPay.P2P.Contracts;

/// <summary>Which way the user is trading.</summary>
public enum P2PSide
{
    /// <summary>User gives the wallet currency and receives local money.</summary>
    Sell,

    /// <summary>User pays in local money and receives the wallet currency.</summary>
    Buy,
}

/// <summary>
/// Where a trade is.
/// </summary>
/// <remarks>
/// <para>
/// A trade is not instant, whatever the word "instant exchange" suggests. It
/// has a leg that happens outside IslaPay — somebody sends or receives money
/// through Transfermóvil — and a person has to confirm it. The statuses are
/// that wait made explicit.
/// </para>
/// <para>
/// <see cref="Settling"/> is the same device the marketplace uses: the ledger
/// owns its transaction, so moving money and recording that it moved are two
/// commits, and this is the status that exists between them. It carries an
/// intent, because three different movements can be in flight.
/// </para>
/// </remarks>
public static class P2PTradeStatuses
{
    /// <summary>A sell whose commit posting may or may not have been written.</summary>
    public const string Pending = "pending";

    /// <summary>A sell: IslaPay owes the user local money, and an operator must send it.</summary>
    public const string AwaitingPayout = "awaiting_payout";

    /// <summary>A buy: the user owes IslaPay local money and has been told where to send it.</summary>
    public const string AwaitingPayment = "awaiting_payment";

    /// <summary>A terminal posting is in flight. See <see cref="P2PSettleIntents"/>.</summary>
    public const string Settling = "settling";

    /// <summary>Paid out, or credited. The trade did what it said.</summary>
    public const string Completed = "completed";

    /// <summary>A sell the operator could not pay. The user has their wallet money back.</summary>
    public const string Refunded = "refunded";

    /// <summary>A buy nobody paid. Nothing ever moved.</summary>
    public const string Expired = "expired";

    /// <summary>Called off before anything was committed.</summary>
    public const string Cancelled = "cancelled";

    /// <summary>Every status, for validating one that arrives as text.</summary>
    public static IReadOnlySet<string> All { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        Pending, AwaitingPayout, AwaitingPayment, Settling,
        Completed, Refunded, Expired, Cancelled,
    };

    /// <summary>Statuses from which no further movement is possible.</summary>
    public static IReadOnlySet<string> Terminal { get; } =
        new HashSet<string>(StringComparer.Ordinal) { Completed, Refunded, Expired, Cancelled };

    /// <summary>Statuses an operator can still act on.</summary>
    public static IReadOnlySet<string> Open { get; } =
        new HashSet<string>(StringComparer.Ordinal) { AwaitingPayout, AwaitingPayment };
}

/// <summary>What a <see cref="P2PTradeStatuses.Settling"/> trade is settling into.</summary>
public static class P2PSettleIntents
{
    /// <summary>A sell's local money is leaving for the user.</summary>
    public const string Payout = "payout";

    /// <summary>A buy's local money arrived; the user's wallet is being credited.</summary>
    public const string Credit = "credit";

    /// <summary>A sell could not be paid; the user's wallet money is going back.</summary>
    public const string Refund = "refund";
}

/// <summary>
/// A local currency the instant-exchange market trades against.
/// </summary>
/// <remarks>
/// A rail is a currency, not a channel: there is one <c>CUP</c>, whichever app
/// the peso moves through. The wallet side is any currency a customer holds
/// (E-ISLA, USDT, USDC), each with its own prices in <see cref="Rates"/>.
/// </remarks>
/// <param name="Id">Stable identifier, sent back on a quote and a trade. The currency code in lower case.</param>
/// <param name="Name">Display name, e.g. <c>CUP</c>.</param>
/// <param name="Code">Local currency code shown on the avatar, e.g. <c>CUP</c>.</param>
/// <param name="Available">
/// Whether the rail can be traded right now. A rail that is temporarily down
/// stays in the list, greyed, rather than vanishing — a method disappearing
/// without explanation reads as a bug to the user. False also when no wallet
/// currency has a price.
/// </param>
/// <param name="Minimum">Smallest trade, in the local currency.</param>
/// <param name="Maximum">Largest trade, in the local currency.</param>
/// <param name="Rates">
/// One entry per wallet currency that has at least one price. A currency
/// missing here cannot be traded on this rail at all.
/// </param>
public sealed record P2PMethodDto(
    string Id,
    string Name,
    string Code,
    bool Available,
    Money Minimum,
    Money Maximum,
    IReadOnlyList<P2PMethodRateDto> Rates);

/// <summary>The prices of one wallet currency on one rail.</summary>
/// <param name="Currency">The wallet currency, e.g. <c>EISLA</c>.</param>
/// <param name="SellRate">
/// Units of local currency per one wallet unit when the user sells, as a
/// decimal string; null when IslaPay is not buying that currency here.
/// </param>
/// <param name="BuyRate">
/// The same for a buy, and deliberately a different number: the spread between
/// the two is where the rail's cost lives. Null when IslaPay is not selling.
/// A rail with only one of the two is one-way, which is a choice the operator
/// makes by publishing only that side.
/// </param>
public sealed record P2PMethodRateDto(string Currency, string? SellRate, string? BuyRate);

/// <summary>
/// A rail as the operator sees it: everything the market shows plus what only
/// the desk edits.
/// </summary>
/// <param name="Available">The switch as the operator set it, whatever the prices say.</param>
public sealed record P2PAdminMethodDto(
    string Id,
    string Name,
    string Code,
    bool Available,
    string Instructions,
    Money Minimum,
    Money Maximum,
    IReadOnlyList<P2PMethodRateDto> Rates,
    DateTimeOffset UpdatedAt);

/// <summary><c>POST /v1/admin/p2p/methods</c>.</summary>
/// <remarks>
/// A new rail starts switched off and with no prices, so creating it never
/// starts it quoting. The id is the currency code in lower case.
/// </remarks>
/// <param name="Currency">
/// The local currency, which must be a fiat currency switched on in the
/// catalogue.
/// </param>
/// <param name="Name">Display name; the currency code when omitted.</param>
/// <param name="Minimum">Smallest trade, in <paramref name="Currency"/>.</param>
/// <param name="Maximum">Largest trade, in <paramref name="Currency"/>.</param>
public sealed record P2PMethodCreate(
    string Currency,
    Money Minimum,
    Money Maximum,
    string? Name = null,
    string? Instructions = null);

/// <summary><c>PATCH /v1/admin/p2p/methods/{id}</c>. A field left out stays as it is.</summary>
/// <param name="Minimum">In the rail's local currency.</param>
/// <param name="Maximum">In the rail's local currency.</param>
public sealed record P2PMethodUpdate(
    string? Name = null,
    Money? Minimum = null,
    Money? Maximum = null);

/// <summary><c>POST /v1/p2p/quotes</c>. Costs nothing and commits to nothing.</summary>
/// <param name="Amount">
/// Amount in the wallet currency, before the fee. Its currency picks which of
/// the rail's prices applies.
/// </param>
public sealed record P2PQuoteRequest(P2PSide Side, Money Amount, string MethodId);

/// <summary>
/// What a trade would look like if it were placed now.
/// </summary>
/// <remarks>
/// Not stored. There is no quote id and nothing to expire, because the price
/// protection is on the other side: a trade may carry the rate it was quoted
/// at, and is refused if the rate has moved since. That gives the same
/// guarantee as a stored quote without a second lifecycle to get wrong.
/// </remarks>
/// <param name="Local">
/// What the user receives (a sell) or must send (a buy), in local currency.
/// </param>
/// <param name="Executable">
/// Whether it could be placed. False with a <paramref name="Reason"/> is a
/// **successful response** describing a trade that cannot happen — the fund
/// is short, or the amount is outside the rail's limits — not an error. See
/// D2: the fund's balance no longer travels, so this is how the client learns
/// about solvency.
/// </param>
public sealed record P2PQuoteDto(
    P2PSide Side,
    string MethodId,
    string MethodName,
    Money Amount,
    Money Fee,
    Money Local,
    string Rate,
    bool Executable,
    string? Reason);

/// <summary>
/// <c>POST /v1/p2p/trades</c>. Requires an <c>Idempotency-Key</c>.
/// </summary>
/// <remarks>
/// This is instant exchange against IslaPay, not a user-to-user market: there
/// is no counterparty and no dispute, because IslaPay is the other side at a
/// published rate (D6). A peer market is a later, separate surface.
/// <para>
/// "Instant" describes the price, not the settlement. A sell debits the wallet
/// at once and the local money follows when an operator sends it; a buy posts
/// nothing at all until an operator confirms the local money arrived. Money is
/// never credited before it exists.
/// </para>
/// </remarks>
/// <param name="Side">Sell or buy.</param>
/// <param name="Amount">Amount in the wallet currency, before the fee.</param>
/// <param name="MethodId">The rail, from <see cref="P2PMethodDto.Id"/>.</param>
/// <param name="QuotedRate">
/// The rate the user was shown, if any. When present and no longer current the
/// trade is refused with <c>quote_expired</c> rather than filled at a price
/// nobody agreed to.
/// </param>
/// <param name="PayoutTo">
/// On a sell, where the local money goes — the card or phone the rail pays
/// into. Required there: a sale that takes somebody's money without saying
/// where to send the pesos leaves the operator nobody to pay. Ignored on a
/// buy.
/// </param>
public sealed record P2PTradeRequest(
    P2PSide Side,
    Money Amount,
    string MethodId,
    string? QuotedRate = null,
    string? PayoutTo = null);

/// <summary>A trade, from the user's side.</summary>
/// <param name="Reference">
/// Short, unique, and the thing both sides quote. On a buy the user writes it
/// on the transfer so the operator can match it; on a sell it is what they
/// give support when asking where their money is.
/// </param>
/// <param name="Instructions">
/// On a buy, where to send the local money. Null on a sell, where IslaPay is
/// the one sending.
/// </param>
/// <param name="FailureReason">Why a refund happened, in the operator's words.</param>
/// <param name="PayoutTo">On a sell, where the local money is being sent.</param>
public sealed record P2PTradeDto(
    string Id,
    P2PSide Side,
    string MethodId,
    string MethodName,
    Money Amount,
    Money Fee,
    Money Local,
    string Rate,
    string Status,
    string Reference,
    string? Instructions,
    string? FailureReason,
    DateTimeOffset CreatedAt,
    DateTimeOffset ExpiresAt,
    DateTimeOffset? SettledAt,
    string? PayoutTo = null);

// ------------------------------------------------------------------ operator

/// <summary>
/// One trade as the person settling it sees it.
/// </summary>
/// <remarks>
/// Carries the user's name and the reference because the operator is about to
/// type them into a banking app, and carries the local amount as the figure to
/// send rather than as something to recompute from a rate.
/// </remarks>
public sealed record P2PQueueItemDto(
    string Id,
    P2PSide Side,
    string MethodId,
    string MethodName,
    string UserId,
    string UserName,
    Money Amount,
    Money Local,
    string Status,
    string Reference,
    DateTimeOffset CreatedAt,
    TimeSpan Waiting,
    string? PayoutTo = null,
    DateTimeOffset? ExpiresAt = null,
    DateTimeOffset? SettledAt = null,
    string? OperatorReference = null,
    string? FailureReason = null);

/// <summary>
/// <c>POST /v1/admin/p2p/trades/{id}/settle</c>. Requires an
/// <c>Idempotency-Key</c>.
/// </summary>
/// <param name="Reference">
/// The bank's or the app's own confirmation number. Required: without it a
/// dispute six weeks later has nothing to check against.
/// </param>
public sealed record P2PSettleRequest(string Reference);

/// <summary><c>POST /v1/admin/p2p/trades/{id}/fail</c>.</summary>
/// <param name="Reason">
/// Shown to the user. "The account number was wrong" is worth saying; an
/// internal code is not.
/// </param>
public sealed record P2PFailRequest(string Reason);

/// <summary><c>PUT /v1/admin/p2p/methods/{id}/instructions</c>.</summary>
/// <param name="Instructions">
/// Where a buyer sends the local money: the account or phone, the name on it,
/// and what to write in the transfer's note. Shown to the buyer as written,
/// next to the trade's reference. Empty clears it.
/// </param>
/// <remarks>
/// Until this existed the rail's instructions were a column nothing could
/// write, so every buy told its buyer to pay and not where.
/// </remarks>
public sealed record P2PInstructionsUpdate(string Instructions);

/// <summary><c>PUT /v1/admin/p2p/rates</c>.</summary>
/// <remarks>
/// Never an update in place. Each call appends a row with an effective time,
/// so a trade settled last Tuesday can still be shown at the rate it was
/// actually priced at.
/// </remarks>
/// <param name="Rate">
/// Units of local currency per one wallet unit. Null or empty withdraws the
/// price, which is how a side stops being offered: another row, not a
/// deletion, so the history still says when it stopped.
/// </param>
public sealed record P2PRateUpdate(
    string MethodId,
    P2PSide Side,
    string WalletCurrency,
    string? Rate);
