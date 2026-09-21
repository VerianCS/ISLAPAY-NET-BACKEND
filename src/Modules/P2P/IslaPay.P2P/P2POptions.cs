namespace IslaPay.P2P;

/// <summary>Settings for the instant-exchange market, bound from <c>P2P</c>.</summary>
public sealed class P2POptions
{
    /// <summary>
    /// IslaPay's cut, in basis points. 100 = 1%.
    /// </summary>
    /// <remarks>
    /// Always taken on the wallet side, never the local one, so that the
    /// figure a user sees in CUP is the figure that arrives. It is also where
    /// the rail's own cost has to come from, alongside the spread between the
    /// buy and sell rates.
    /// </remarks>
    public int FeeBps { get; init; } = 100;

    /// <summary>
    /// How long a buyer has to send their local money before the trade stops
    /// being expected.
    /// </summary>
    /// <remarks>
    /// Expiry here means "stop waiting", not "refuse to honour". A buyer who
    /// sends at minute 59 and an operator who looks at minute 65 must still be
    /// able to complete, or IslaPay is holding money it will not account for —
    /// see <c>P2PService.ConfirmReceiptAsync</c>.
    /// </remarks>
    public TimeSpan PaymentWindow { get; init; } = TimeSpan.FromHours(2);

    /// <summary>
    /// How long a seller's payout may sit unattended before it is flagged.
    /// </summary>
    /// <remarks>
    /// Nothing expires on this. A committed sell has taken the user's money
    /// and the only honest endings are paying them or giving it back, neither
    /// of which a timer can decide. It exists to sort the operator's queue and
    /// to make a stuck trade visible.
    /// </remarks>
    public TimeSpan PayoutTarget { get; init; } = TimeSpan.FromHours(4);

    /// <summary>How often the sweeper finishes interrupted movements.</summary>
    public TimeSpan SweepInterval { get; init; } = TimeSpan.FromMinutes(1);

    /// <summary>
    /// How long an in-flight trade is left alone before the sweeper treats it
    /// as abandoned.
    /// </summary>
    public TimeSpan InFlightGrace { get; init; } = TimeSpan.FromMinutes(2);

    /// <summary>
    /// How much of the settlement fund to keep back from quoting.
    /// </summary>
    /// <remarks>
    /// A reserve, in minor units of whichever currency is being checked. The
    /// fund is a platform account and may go negative, so nothing in the
    /// ledger stops IslaPay committing to a payout it cannot make; this is the
    /// margin between "we can just about cover this" and "we can cover this
    /// and the two trades already in the queue".
    /// </remarks>
    public long FundReserveMinor { get; init; }

    /// <summary>Whether a phone has to be proved before money moves. See D11.</summary>
    public bool RequireVerifiedPhone { get; init; } = true;
}
