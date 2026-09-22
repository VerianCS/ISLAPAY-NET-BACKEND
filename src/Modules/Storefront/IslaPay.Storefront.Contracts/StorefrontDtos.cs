using IslaPay.Platform;

namespace IslaPay.Storefront.Contracts;

/// <summary>
/// Where a storefront order's money is.
/// </summary>
/// <remarks>
/// <para>
/// The same seven-state shape the marketplace uses, plus one: a shop order has
/// a middle the marketplace does not. In the marketplace two people meet and
/// hand something over; here a merchant has to pack and dispatch first, and a
/// buyer staring at a screen deserves to be told which of those is happening.
/// </para>
/// <para>
/// <c>releasing</c> and <c>refunding</c> are not decoration. The order row and
/// the ledger commit separately, so moving money and recording that it moved
/// are two writes. Those two states are what sits between them, and they are
/// what lets the sweeper tell "the money never moved" from "the money moved
/// and we died before writing it down" — which need opposite repairs.
/// </para>
/// </remarks>
public static class StoreOrderStatuses
{
    /// <summary>The order exists; the hold may or may not have posted yet.</summary>
    public const string Pending = "pending";

    /// <summary>Paid for. The money is in escrow and the merchant is packing.</summary>
    public const string Paid = "paid";

    /// <summary>
    /// On its way, or waiting at the pickup point.
    /// </summary>
    /// <remarks>
    /// One status for both, because it means the same thing to the money: the
    /// merchant has done their part and the buyer has not confirmed. Which of
    /// the two it looks like is <see cref="StoreOrderDto.Delivery"/>'s to say.
    /// </remarks>
    public const string Dispatched = "dispatched";

    /// <summary>The payout posting is in flight.</summary>
    public const string Releasing = "releasing";

    /// <summary>A refund posting is in flight.</summary>
    public const string Refunding = "refunding";

    /// <summary>Received by the buyer, and the merchant has been paid.</summary>
    public const string Completed = "completed";

    /// <summary>Called off; the buyer has their money back.</summary>
    public const string Cancelled = "cancelled";

    /// <summary>Nobody collected it in time; the buyer has their money back.</summary>
    public const string Expired = "expired";

    /// <summary>Statuses from which no further movement is possible.</summary>
    public static IReadOnlySet<string> Terminal { get; } =
        new HashSet<string>(StringComparer.Ordinal) { Completed, Cancelled, Expired };

    /// <summary>Statuses where the buyer's money is genuinely in escrow.</summary>
    public static IReadOnlySet<string> Escrowed { get; } =
        new HashSet<string>(StringComparer.Ordinal) { Paid, Dispatched };
}

/// <summary>How the goods reach the buyer.</summary>
public static class DeliveryMethods
{
    /// <summary>To an address. Carries a fee, and the buyer confirms receipt.</summary>
    public const string Home = "home";

    /// <summary>
    /// Collected at a point. Free, and released by a code.
    /// </summary>
    /// <remarks>
    /// The code is the same idea as the marketplace's: proof held by the buyer
    /// that they have the goods. Somebody at the counter scanning it is what
    /// pays the merchant, and a buyer who never shows up gets refunded.
    /// </remarks>
    public const string Pickup = "pickup";

    public static bool IsKnown(string? value) =>
        value is Home or Pickup;
}

/// <summary>A shop, as a buyer sees it.</summary>
/// <remarks>
/// Carries no contact details. The market is browsable by every account, and
/// an endpoint that hands out addresses is a scraping target regardless of
/// what it was built for — the same reasoning as the marketplace's seller.
/// </remarks>
public sealed record MerchantDto(string Id, string Name, bool Active);

/// <param name="FromPrice">
/// The cheapest thing in it, for the "desde $X" label. Null when the category
/// is empty, which is a different thing from free.
/// </param>
public sealed record StoreCategoryDto(
    string Id,
    string Name,
    string Icon,
    int SortOrder,
    Money? FromPrice);

/// <param name="Stock">
/// How many are left. Sent so a client can say "last one" rather than
/// discovering it at checkout, and checked again server-side when the order is
/// placed — a figure a client read seconds ago is not a reservation.
/// </param>
public sealed record ProductDto(
    string Id,
    string MerchantId,
    string MerchantName,
    string CategoryId,
    string Name,
    string Description,
    string Icon,
    Money Price,
    int Stock,
    bool Available);

public sealed record PickupPointDto(
    string Id,
    string Name,
    string Address,
    string Hours,
    bool Active);

/// <summary>One product and how many of it.</summary>
public sealed record CartLineRequest(Guid ProductId, int Quantity);

/// <summary><c>POST /v1/store/quotes</c>.</summary>
/// <remarks>
/// <para>
/// The client sends ids and counts, never money. Everything a total is made of
/// — unit prices, the delivery fee, the commission — is priced here, because a
/// client that computes its own total is a client that can be out of date, and
/// one that <i>sends</i> its own total is a client that can be lied to by
/// whoever is holding the phone.
/// </para>
/// <para>
/// A quote commits to nothing and takes no idempotency key: there is nothing a
/// repeat could duplicate.
/// </para>
/// </remarks>
public sealed record StoreQuoteRequest(
    IReadOnlyList<CartLineRequest> Lines,
    string Delivery);

/// <param name="Unit">The price each, as it stands now.</param>
/// <param name="Subtotal">
/// <c>Unit</c> times <c>Quantity</c>, sent rather than left as arithmetic so
/// that the figure on the receipt is not something two clients can each round
/// differently.
/// </param>
public sealed record StoreQuoteLine(
    string ProductId,
    string Name,
    Money Unit,
    int Quantity,
    Money Subtotal,
    int Stock);

/// <param name="Shipping">
/// Zero for a pickup. It is the platform's, not the merchant's — IslaPay is
/// what pays whoever carries the box.
/// </param>
/// <param name="Commission">
/// What IslaPay keeps, taken from the merchant's side. The buyer pays
/// <c>Total</c> either way; this says how it splits.
/// </param>
/// <param name="MerchantReceives"><c>Goods</c> less <c>Commission</c>.</param>
/// <param name="Problems">
/// Why this cart cannot be ordered as it stands — out of stock, withdrawn, a
/// merchant switched off. Empty means it can.
/// </param>
public sealed record StoreQuoteDto(
    IReadOnlyList<StoreQuoteLine> Lines,
    Money Goods,
    Money Shipping,
    Money Total,
    Money Commission,
    Money MerchantReceives,
    string Delivery,
    IReadOnlyList<string> Problems)
{
    public bool CanOrder => Problems.Count == 0;
}

/// <summary><c>POST /v1/store/orders</c>. Requires an <c>Idempotency-Key</c>.</summary>
/// <param name="Address">Required for a home delivery, ignored for a pickup.</param>
/// <param name="PickupPointId">The other way round.</param>
public sealed record PlaceStoreOrderRequest(
    IReadOnlyList<CartLineRequest> Lines,
    string Delivery,
    string? Address = null,
    Guid? PickupPointId = null,
    string? Note = null);

/// <summary>A line as it was ordered.</summary>
/// <remarks>
/// The name and the unit price are copied, not joined. A receipt has to keep
/// saying what was bought and what it cost on the day; a merchant repricing a
/// product next week must not rewrite somebody's history.
/// </remarks>
public sealed record StoreOrderLineDto(
    string ProductId,
    string Name,
    Money Unit,
    int Quantity,
    Money Subtotal);

/// <param name="Code">
/// The pickup code, and only for a pickup and only for the buyer. Somebody at
/// the counter has to be shown it before the merchant is paid.
/// </param>
/// <param name="ExpiresAt">
/// When an uncollected order refunds itself. Set for both methods: a parcel
/// that never arrives should not leave a buyer's money in escrow for ever.
/// </param>
public sealed record StoreOrderDto(
    string Id,
    string Reference,
    string BuyerId,
    string BuyerName,
    string MerchantId,
    string MerchantName,
    IReadOnlyList<StoreOrderLineDto> Lines,
    Money Goods,
    Money Shipping,
    Money Total,
    Money Commission,
    Money MerchantReceives,
    string Delivery,
    string Address,
    string? PickupPointId,
    string? PickupPointName,
    string Note,
    string Status,
    DateTimeOffset CreatedAt,
    DateTimeOffset ExpiresAt,
    DateTimeOffset? DispatchedAt,
    DateTimeOffset? CompletedAt,
    string? Code);

/// <summary><c>POST /v1/store/orders/{id}/dispatch</c>. The merchant's.</summary>
public sealed record DispatchRequest(string? Tracking = null);

/// <summary><c>POST /v1/store/orders/collect</c>. Requires an <c>Idempotency-Key</c>.</summary>
public sealed record CollectRequest(string Code);
