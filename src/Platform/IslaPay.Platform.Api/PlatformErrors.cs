namespace IslaPay.Platform.Api;

/// <summary>
/// Error codes the platform itself emits, before any module is reached.
/// </summary>
/// <remarks>
/// <para>
/// Only what is genuinely cross-cutting lives here: a body that could not be
/// read, a token the bearer middleware rejected, an idempotency key misused.
/// Every other code belongs to the module that raises it — see
/// <c>IdentityErrors</c>, <c>WalletErrors</c>, <c>ExchangeErrors</c>.
/// </para>
/// <para>
/// Keeping them apart is what lets a module be lifted out later. A single
/// catalogue would mean Wallet adding a code changes the assembly Identity
/// links, which is how independent deployment is lost one constant at a time.
/// </para>
/// <para>
/// These are strings, not an enum, and deliberately so: the client maps a code
/// it recognises onto a typed failure and anything else onto a generic one, so
/// the server can introduce a code without waiting for an app release.
/// </para>
/// </remarks>
public static class PlatformErrors
{
    /// <summary>
    /// The body was not readable JSON, or not the shape the route takes.
    /// Always a client bug; retrying the same bytes will not help.
    /// </summary>
    public const string MalformedRequest = "malformed_request";

    /// <summary>
    /// Missing, malformed, expired or rejected bearer token, and the same for
    /// a refresh token the provider will not exchange. The client's response
    /// is identical in every case: refresh once, then sign out.
    /// </summary>
    public const string TokenInvalid = "token_invalid";

    /// <summary>
    /// Authenticated, and not allowed to do this. Distinct from
    /// <see cref="TokenInvalid"/>, where refreshing might help.
    /// </summary>
    public const string Forbidden = "forbidden";

    /// <summary>
    /// An <c>Idempotency-Key</c> was reused with a different body. This is
    /// always a client bug — retrying will not help, so the client must not.
    /// </summary>
    public const string IdempotencyKeyReuse = "idempotency_key_reuse";

    /// <summary>
    /// A request with this key is still running. Honour <c>Retry-After</c>.
    /// </summary>
    public const string RequestInFlight = "request_in_flight";
}
