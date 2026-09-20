using IslaPay.Platform;

namespace IslaPay.Marketplace.Contracts;

/// <summary>
/// What a listing can be. A closed set, because the transitions between them
/// are the product: an item that is reserved is not on sale, and one that is
/// sold never is again.
/// </summary>
public static class ListingStatuses
{
    /// <summary>On offer. The only status a buyer can act on.</summary>
    public const string Active = "active";

    /// <summary>Someone has money locked against it. Nobody else can buy it.</summary>
    public const string Reserved = "reserved";

    /// <summary>Paid for and collected.</summary>
    public const string Sold = "sold";

    /// <summary>Taken down by the seller.</summary>
    public const string Withdrawn = "withdrawn";
}

/// <summary>
/// Where an order's money is.
/// </summary>
/// <remarks>
/// <para>
/// Seven, not four, and the three extra ones are the point. The order row and
/// the ledger are two databases as far as atomicity goes — the ledger owns its
/// own transaction — so every movement of money is bracketed by a status that
/// says "a posting for this is in flight". A process that dies mid-flight
/// leaves that status behind, which is what lets the sweeper work out what
/// actually happened instead of guessing.
/// </para>
/// <para>
/// Without them, a crash between "money moved" and "we wrote down that it
/// moved" is indistinguishable from "money never moved", and the recovery for
/// one is a double payout in the other.
/// </para>
/// </remarks>
public static class OrderStatuses
{
    /// <summary>The order exists; the hold may or may not have posted yet.</summary>
    public const string Pending = "pending";

    /// <summary>The buyer's money is in escrow, waiting for the code to be scanned.</summary>
    public const string Held = "held";

    /// <summary>The release posting is in flight.</summary>
    public const string Releasing = "releasing";

    /// <summary>A refund posting is in flight.</summary>
    public const string Refunding = "refunding";

    /// <summary>The seller scanned the code and has been paid.</summary>
    public const string Released = "released";

    /// <summary>Called off by one of the two parties; the buyer has their money back.</summary>
    public const string Cancelled = "cancelled";

    /// <summary>Nobody scanned the code in time; the buyer has their money back.</summary>
    public const string Expired = "expired";

    /// <summary>Statuses from which no further movement is possible.</summary>
    public static IReadOnlySet<string> Terminal { get; } =
        new HashSet<string>(StringComparer.Ordinal) { Released, Cancelled, Expired };
}

/// <summary><c>POST /v1/listings</c>.</summary>
/// <param name="Title">What is for sale. Required.</param>
/// <param name="Description">Free text. Optional.</param>
/// <param name="Category">From the client's list. Not validated against a
/// server-side taxonomy: there isn't an agreed one yet, and inventing one here
/// would be a second source of truth for a list the app already owns.</param>
/// <param name="Condition">New, used, for parts — same reasoning.</param>
/// <param name="Price">What the buyer pays. The seller receives this less the
/// commission.</param>
/// <param name="Location">Where the item is, for a market where people meet to
/// hand goods over. Optional.</param>
/// <param name="Photos">
/// URLs, not uploads. There is no image storage in this build, so this is the
/// shape the endpoint will keep rather than a feature it delivers; a client
/// with nowhere to upload to sends an empty list.
/// </param>
public sealed record PublishListingRequest(
    string Title,
    string? Description,
    string Category,
    string Condition,
    Money Price,
    string? Location = null,
    IReadOnlyList<string>? Photos = null);

/// <summary>A listing as anyone may see it.</summary>
/// <remarks>
/// Carries the seller's display name but never their e-mail: the market is
/// browsable by every account, and an endpoint that hands out addresses is a
/// scraping target regardless of what it was built for.
/// </remarks>
public sealed record ListingDto(
    string Id,
    string SellerId,
    string SellerName,
    string Title,
    string Description,
    string Category,
    string Condition,
    Money Price,
    string Location,
    IReadOnlyList<string> Photos,
    string Status,
    DateTimeOffset PublishedAt);

/// <summary><c>POST /v1/orders</c>. Requires an <c>Idempotency-Key</c>.</summary>
/// <param name="ListingId">What the buyer is locking money against.</param>
public sealed record PlaceOrderRequest(Guid ListingId);

/// <summary><c>POST /v1/orders/redeem</c>. Requires an <c>Idempotency-Key</c>.</summary>
/// <param name="Code">
/// What the seller scanned or typed. Accepted in any case, with or without the
/// dashes, and with the digits a human confuses for letters folded back.
/// </param>
public sealed record RedeemOrderRequest(string Code);

/// <summary>An order, from whichever side is asking.</summary>
/// <param name="Amount">What leaves the buyer, and what sits in escrow.</param>
/// <param name="Fee">IslaPay's commission, taken from the seller's side.</param>
/// <param name="SellerReceives"><c>Amount</c> less <c>Fee</c>. Sent rather
/// than left as arithmetic, because the number the seller cares about should
/// not be something two clients can each round differently.</param>
/// <param name="ExpiresAt">When an unscanned hold returns to the buyer.</param>
/// <param name="Code">
/// The code the seller has to scan. Present only for the buyer: it is the
/// buyer's proof that they are satisfied, and a seller who could read it could
/// collect without handing anything over — which is the single thing this
/// whole mechanism exists to prevent.
/// </param>
public sealed record OrderDto(
    string Id,
    string ListingId,
    string ListingTitle,
    string BuyerId,
    string BuyerName,
    string SellerId,
    string SellerName,
    Money Amount,
    Money Fee,
    Money SellerReceives,
    string Status,
    DateTimeOffset CreatedAt,
    DateTimeOffset ExpiresAt,
    string? Code);
