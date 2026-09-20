namespace IslaPay.Marketplace;

/// <summary>Settings for the marketplace, bound from the <c>Marketplace</c> section.</summary>
public sealed class MarketplaceOptions
{
    /// <summary>
    /// IslaPay's commission on a completed sale, in basis points. 100 = 1%.
    /// </summary>
    /// <remarks>
    /// Taken from the seller's side: the buyer pays the listed price and the
    /// seller receives it less this. Stated on the order before anything is
    /// locked, so neither party finds out afterwards.
    /// <para>
    /// A commission that rounds to nothing on a small sale is not charged, and
    /// the fee leg is left off the posting entirely rather than written as a
    /// zero — the ledger refuses a zero entry, which is how the same bug was
    /// found in conversions.
    /// </para>
    /// </remarks>
    public int FeeBps { get; init; } = 100;

    /// <summary>
    /// How long a buyer's money stays locked before it returns on its own.
    /// </summary>
    /// <remarks>
    /// Three days. Long enough to arrange to meet somebody across a province
    /// without the seller losing the sale; short enough that a buyer whose
    /// seller vanished is not left without their balance over a long weekend.
    /// </remarks>
    public TimeSpan HoldDuration { get; init; } = TimeSpan.FromHours(72);

    /// <summary>How often the sweeper looks for holds to return and movements to finish.</summary>
    public TimeSpan SweepInterval { get; init; } = TimeSpan.FromMinutes(1);

    /// <summary>
    /// How long an in-flight order is left alone before the sweeper treats it
    /// as abandoned.
    /// </summary>
    /// <remarks>
    /// A <c>pending</c>, <c>releasing</c> or <c>refunding</c> row is normally
    /// in that state for milliseconds. This is the margin that stops the
    /// sweeper from racing a request that is simply still running — it does
    /// not make the repair unsafe, only unnecessary.
    /// </remarks>
    public TimeSpan InFlightGrace { get; init; } = TimeSpan.FromMinutes(2);

    /// <summary>
    /// Whether a phone has to be proved before money moves. See D11.
    /// </summary>
    /// <remarks>
    /// Applies to the buyer at the moment of locking, not to the seller
    /// publishing: putting an item up for sale is not a money movement, and
    /// blocking it would leave the market empty for the sake of a check that
    /// happens at payment anyway.
    /// </remarks>
    public bool RequireVerifiedPhone { get; init; } = true;

    /// <summary>Longest title a listing may carry.</summary>
    public int MaxTitleLength { get; init; } = 120;

    /// <summary>Longest description a listing may carry.</summary>
    public int MaxDescriptionLength { get; init; } = 4000;

    /// <summary>Most photos a listing may carry.</summary>
    public int MaxPhotos { get; init; } = 10;
}
