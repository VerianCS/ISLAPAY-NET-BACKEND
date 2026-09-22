namespace IslaPay.Storefront.Contracts;

/// <summary>
/// The codes this module refuses with.
/// </summary>
/// <remarks>
/// Every one of these is a different sentence on a screen, so every one is a
/// different code. A client that had to read <c>detail</c> to tell "sold out"
/// from "this shop is closed" would break the day somebody rewords it.
/// </remarks>
public static class StorefrontErrors
{
    /// <summary>No such product, or one that is no longer listed.</summary>
    public const string ProductNotFound = "product_not_found";

    /// <summary>
    /// Listed, and not for sale right now.
    /// </summary>
    /// <remarks>
    /// Withdrawn, or its merchant switched off. Distinct from
    /// <see cref="OutOfStock"/> because the answers differ: one is "come back
    /// later", the other is "this is not coming back".
    /// </remarks>
    public const string ProductUnavailable = "product_unavailable";

    /// <summary>Fewer left than asked for. <c>meta.available</c> says how many.</summary>
    public const string OutOfStock = "out_of_stock";

    /// <summary>The cart is empty, or a line asks for none.</summary>
    public const string InvalidCart = "invalid_cart";

    /// <summary>
    /// The cart spans two shops.
    /// </summary>
    /// <remarks>
    /// One order pays one merchant, so a basket from two of them is two
    /// orders. Refused rather than split silently: a buyer who thinks they
    /// paid once and finds two charges has been surprised by their own
    /// money.
    /// </remarks>
    public const string MixedMerchants = "mixed_merchants";

    /// <summary>A home delivery with nowhere to deliver to.</summary>
    public const string MissingAddress = "missing_address";

    /// <summary>A pickup with no point chosen, or one that is closed.</summary>
    public const string UnknownPickupPoint = "unknown_pickup_point";

    public const string UnknownDeliveryMethod = "unknown_delivery_method";

    public const string OrderNotFound = "order_not_found";

    /// <summary>The order has moved past the point where this was possible.</summary>
    public const string OrderNotOpen = "order_not_open";

    /// <summary>Nobody collected it in time and it has already refunded.</summary>
    public const string OrderExpired = "order_expired";

    /// <summary>The code matches nothing that is waiting to be collected.</summary>
    public const string CodeInvalid = "code_invalid";

    /// <summary>Only the merchant may dispatch, and only their own order.</summary>
    public const string NotTheMerchant = "not_the_merchant";

    /// <summary>Raised by the ledger. Repeated here so the contract lists it.</summary>
    public const string InsufficientFunds = "insufficient_funds";

    /// <summary>Same gate as every other way money moves.</summary>
    public const string PhoneNotVerified = "phone_not_verified";
}
