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

    // ---------------------------------------------------------------- identity

    /// <summary>
    /// Wrong e-mail or password. Deliberately one code for both: telling the
    /// caller which half was wrong turns the login endpoint into a way to
    /// enumerate registered addresses.
    /// </summary>
    public const string InvalidCredentials = "invalid_credentials";

    /// <summary>
    /// The account exists and the password is right, but the account is
    /// disabled. Distinct from <see cref="InvalidCredentials"/> because the
    /// user can act on it — there is someone to contact — and it is only ever
    /// returned to someone who already proved they own the account.
    /// </summary>
    public const string AccountDisabled = "account_disabled";

    /// <summary>
    /// Missing, malformed, expired or rejected bearer token, and the same for
    /// a refresh token that the provider will not exchange. The client's
    /// response is identical in every case: refresh once, then sign out.
    /// </summary>
    public const string TokenInvalid = "token_invalid";

    /// <summary>Registration with an address that already has an account.</summary>
    public const string EmailTaken = "email_taken";

    /// <summary>
    /// The identity provider's password policy rejected it. <c>detail</c>
    /// carries the provider's reason; the client shows its own copy.
    /// </summary>
    public const string WeakPassword = "weak_password";

    /// <summary>Not a phone number in E.164 form.</summary>
    public const string InvalidPhone = "invalid_phone";

    /// <summary>Registration without a name. Distinct from the card-holder
    /// name: this one names the account, not a piece of plastic.</summary>
    public const string NameRequired = "name_required";

    /// <summary>Wrong code. Attempts are counted — see <see cref="OtpLocked"/>.</summary>
    public const string OtpInvalid = "otp_invalid";

    /// <summary>
    /// The code was right but too old, or no code was outstanding. Separate
    /// from <see cref="OtpInvalid"/> so the client can offer "resend" rather
    /// than "try again", which is the actionable difference.
    /// </summary>
    public const string OtpExpired = "otp_expired";

    /// <summary>
    /// Too many wrong codes; this challenge is dead and a new one must be
    /// requested. Without it, a six-digit code is a million guesses away.
    /// </summary>
    public const string OtpLocked = "otp_locked";

    /// <summary>
    /// Resends are rate-limited. Honour <c>Retry-After</c>: each send costs
    /// real money and is a way to make someone else's phone ring all night.
    /// </summary>
    public const string OtpRateLimited = "otp_rate_limited";

    /// <summary>
    /// The identity provider could not be reached or answered unusably. A
    /// dependency failure, not the caller's fault, and safe to retry.
    /// </summary>
    public const string IdentityUnavailable = "identity_unavailable";

    /// <summary>
    /// The body was not readable JSON, or was not the shape the route takes.
    /// Always a client bug; retrying the same bytes will not help.
    /// </summary>
    public const string MalformedRequest = "malformed_request";

    /// <summary>
    /// Authenticated, and not allowed to do this. Distinct from
    /// <see cref="TokenInvalid"/>, where refreshing might help, and from
    /// <see cref="AccountDisabled"/>, which is about the account rather than
    /// the operation.
    /// </summary>
    public const string Forbidden = "forbidden";

    /// <summary>
    /// The identity provider's brute-force protection has locked this account
    /// out for now. Honour <c>Retry-After</c>; a faster retry extends it.
    /// </summary>
    public const string TooManyAttempts = "too_many_attempts";

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
