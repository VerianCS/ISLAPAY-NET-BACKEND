using IslaPay.Platform;

namespace IslaPay.Marketplace.Contracts;

/// <summary>What Marketplace publishes, and where.</summary>
public static class MarketplaceEvents
{
    /// <summary>The bounded context, and therefore the exchange <c>x.marketplace</c>.</summary>
    public const string Context = "marketplace";

    /// <summary>Every order event, for a consumer that wants the lot.</summary>
    public const string AllOrderEvents = "order.*.v1";

    /// <summary>A buyer's money is in escrow against a listing.</summary>
    public const string OrderHeld = "order.held.v1";

    /// <summary>The code was scanned and the seller has been paid.</summary>
    public const string OrderReleased = "order.released.v1";

    /// <summary>The hold came back to the buyer, called off or timed out.</summary>
    public const string OrderRefunded = "order.refunded.v1";
}

/// <summary>
/// A buyer has locked the price of a listing.
/// </summary>
/// <remarks>
/// Published in the same transaction as the ledger entries that moved the
/// money into escrow, so it cannot announce a hold that does not exist. The
/// seller's notification hangs off this: it is the signal that somebody is on
/// their way to collect.
/// </remarks>
/// <param name="ExpiresAt">
/// Carried on the event so a consumer can schedule a reminder without reading
/// the order back.
/// </param>
public sealed record OrderHeld(
    Guid OrderId,
    Guid ListingId,
    string BuyerId,
    string SellerId,
    Money Amount,
    DateTimeOffset ExpiresAt,
    DateTimeOffset OccurredAt);

/// <summary>The seller scanned the buyer's code and has been paid.</summary>
/// <param name="Amount">What the buyer paid.</param>
/// <param name="Fee">What IslaPay kept. The seller received the difference.</param>
public sealed record OrderReleased(
    Guid OrderId,
    Guid ListingId,
    string BuyerId,
    string SellerId,
    Money Amount,
    Money Fee,
    DateTimeOffset OccurredAt);

/// <summary>The hold went back to the buyer.</summary>
/// <param name="Reason">
/// <c>cancelled</c> or <c>expired</c>. The money is the same either way; what
/// the buyer and the seller should be told is not.
/// </param>
public sealed record OrderRefunded(
    Guid OrderId,
    Guid ListingId,
    string BuyerId,
    string SellerId,
    Money Amount,
    string Reason,
    DateTimeOffset OccurredAt);
