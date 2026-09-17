namespace IslaPay.Contracts;

/// <summary>
/// The machine-readable <c>code</c> carried by every error response.
/// </summary>
/// <remarks>
/// <para>
/// These are strings, not an enum, and deliberately so. The client maps a code
/// it recognises onto a typed failure and anything else onto a generic one, so
/// the server can introduce a code without waiting for an app release. An enum
/// would turn that into a deserialisation failure — the client would break on
/// exactly the responses it most needs to show.
/// </para>
/// <para>
/// The first seven correspond one-to-one with the sealed failure types the
/// Flutter client already models (<c>WalletFailure</c> and <c>CardFailure</c>).
/// Keep them in step: see <c>API_CONTRACT.md</c> §4.
/// </para>
/// </remarks>
public static class ErrorCodes
{
    /// <summary>Zero, negative, or unparseable. Client: <c>InvalidAmount</c>.</summary>
    public const string InvalidAmount = "invalid_amount";

    /// <summary>
    /// The user's balance does not cover it. Client:
    /// <c>InsufficientFunds(currency)</c> — so <c>meta.currency</c> is required.
    /// </summary>
    public const string InsufficientFunds = "insufficient_funds";

    /// <summary>
    /// The settlement fund cannot cover the destination leg. Client:
    /// <c>ExchangeFundUnavailable(currency)</c> — <c>meta.currency</c> required.
    /// </summary>
    public const string FundUnavailable = "fund_unavailable";

    /// <summary>No destination address or e-mail. Client: <c>MissingDestination</c>.</summary>
    public const string MissingDestination = "missing_destination";

    /// <summary>
    /// A card of that currency and kind already exists. Client:
    /// <c>DuplicateCard(currency, kind)</c> — <c>meta.currency</c> and
    /// <c>meta.kind</c> required.
    /// </summary>
    public const string DuplicateCard = "duplicate_card";

    /// <summary>Client: <c>MissingHolderName</c>.</summary>
    public const string MissingHolderName = "missing_holder_name";

    /// <summary>Client: <c>MissingDeliveryAddress</c>.</summary>
    public const string MissingDeliveryAddress = "missing_delivery_address";

    /// <summary>
    /// The quote's <c>expiresAt</c> has passed. The client re-quotes and
    /// retries rather than surfacing this to the user.
    /// </summary>
    public const string QuoteExpired = "quote_expired";

    /// <summary>
    /// An <c>Idempotency-Key</c> was reused with a different body. This is
    /// always a client bug — retrying will not help, so the client must not.
    /// </summary>
    public const string IdempotencyKeyReuse = "idempotency_key_reuse";

    /// <summary>
    /// A request with this key is still running. Honour <c>Retry-After</c>.
    /// </summary>
    public const string RequestInFlight = "request_in_flight";

    /// <summary>
    /// Codes whose <c>meta</c> must carry a <c>currency</c>, because the
    /// client's corresponding type cannot be constructed without one.
    /// </summary>
    public static IReadOnlySet<string> RequireCurrencyMeta { get; } =
        new HashSet<string>(StringComparer.Ordinal)
        {
            InsufficientFunds,
            FundUnavailable,
            DuplicateCard,
        };
}
