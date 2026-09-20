namespace IslaPay.Marketplace.Contracts;

/// <summary>Error codes the Marketplace module raises.</summary>
/// <remarks>
/// Two of these — <see cref="InsufficientFunds"/> and
/// <see cref="PhoneNotVerified"/> — are deliberately the same strings Wallet
/// sends. They are one condition as far as the person holding the phone is
/// concerned, and the client models each as a single failure type; giving the
/// same condition a second code per module would make the client's mapping
/// depend on which endpoint it happened to call.
/// </remarks>
public static class MarketplaceErrors
{
    /// <summary>No listing with that id, or it was never visible to this caller.</summary>
    public const string ListingNotFound = "listing_not_found";

    /// <summary>
    /// The listing exists but cannot be bought: reserved by someone else,
    /// already sold, or withdrawn.
    /// </summary>
    public const string ListingUnavailable = "listing_unavailable";

    /// <summary>An operation only the listing's own seller may perform.</summary>
    public const string NotTheSeller = "not_the_seller";

    /// <summary>
    /// A seller trying to buy their own item. Refused rather than allowed as a
    /// round trip: it would move money out and back through escrow, charge a
    /// commission on nothing, and appear in both parties' history as a sale
    /// that never happened.
    /// </summary>
    public const string SelfPurchase = "self_purchase";

    /// <summary>Not the buyer's or the seller's order, or no such order.</summary>
    public const string OrderNotFound = "order_not_found";

    /// <summary>
    /// The order is no longer holding money — it has been paid out, called
    /// off, or timed out. Distinct from <see cref="OrderNotFound"/> because
    /// the caller is entitled to know, and the client shows a different screen.
    /// </summary>
    public const string OrderNotHeld = "order_not_held";

    /// <summary>The hold timed out before anyone scanned the code.</summary>
    public const string OrderExpired = "order_expired";

    /// <summary>
    /// The code does not match a live order of the caller's.
    /// </summary>
    /// <remarks>
    /// Deliberately one code for four different situations: no such code, a
    /// code belonging to somebody else's sale, an expired one, and one already
    /// scanned. Telling them apart would turn this endpoint into an oracle for
    /// guessing codes — a "wrong seller" answer confirms the code is real.
    /// </remarks>
    public const string CodeInvalid = "code_invalid";

    /// <summary>Zero, negative, or beyond what the currency can express.</summary>
    public const string InvalidPrice = "invalid_price";

    /// <summary>A listing with no title, or nothing but whitespace.</summary>
    public const string InvalidListing = "invalid_listing";

    /// <summary>
    /// The buyer's balance does not cover the price. Client:
    /// <c>InsufficientFunds(currency)</c>, so <c>meta.currency</c> is required.
    /// </summary>
    public const string InsufficientFunds = "insufficient_funds";

    /// <summary>Money cannot move until the account's phone is proved (D11).</summary>
    public const string PhoneNotVerified = "phone_not_verified";

    /// <summary>
    /// Codes whose <c>meta</c> must carry a <c>currency</c>, because the
    /// client's corresponding type cannot be constructed without one.
    /// </summary>
    public static IReadOnlySet<string> RequireCurrencyMeta { get; } =
        new HashSet<string>(StringComparer.Ordinal) { InsufficientFunds };
}
