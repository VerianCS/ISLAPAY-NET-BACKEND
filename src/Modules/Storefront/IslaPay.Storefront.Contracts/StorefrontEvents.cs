using IslaPay.Platform;

namespace IslaPay.Storefront.Contracts;

/// <summary>
/// What this module announces, and the routing keys it announces them under.
/// </summary>
/// <remarks>
/// Published through the outbox, inside the same transaction as the posting
/// they describe. That is the only way the event and the money cannot
/// disagree: enqueuing afterwards is a second write that can fail on its own,
/// and an announced payout that did not happen is worse than a quiet one.
/// </remarks>
public static class StorefrontEvents
{
    public const string Context = "storefront";

    /// <summary>Money is in escrow and the merchant has something to pack.</summary>
    public const string OrderPaid = "store.order.paid.v1";

    /// <summary>On its way, or waiting at the counter.</summary>
    public const string OrderDispatched = "store.order.dispatched.v1";

    /// <summary>Received, and the merchant has been paid.</summary>
    public const string OrderCompleted = "store.order.completed.v1";

    /// <summary>Called off or timed out; the buyer has their money back.</summary>
    public const string OrderRefunded = "store.order.refunded.v1";
}

/// <param name="Total">What left the buyer, goods and shipping together.</param>
public sealed record StoreOrderPaid(
    Guid OrderId,
    string Reference,
    string BuyerId,
    string MerchantId,
    Money Total,
    string Delivery,
    DateTimeOffset At);

public sealed record StoreOrderDispatched(
    Guid OrderId,
    string Reference,
    string BuyerId,
    string MerchantId,
    DateTimeOffset At);

/// <param name="MerchantReceives">The goods less the commission.</param>
public sealed record StoreOrderCompleted(
    Guid OrderId,
    string Reference,
    string BuyerId,
    string MerchantId,
    Money MerchantReceives,
    Money Commission,
    DateTimeOffset At);

/// <param name="Reason"><c>cancelled</c> or <c>expired</c>.</param>
public sealed record StoreOrderRefunded(
    Guid OrderId,
    string Reference,
    string BuyerId,
    Money Total,
    string Reason,
    DateTimeOffset At);
