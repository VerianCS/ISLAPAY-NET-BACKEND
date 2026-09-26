namespace IslaPay.Identity.Contracts;

/// <summary>
/// The token pair every successful authentication returns.
/// </summary>
/// <remarks>
/// <para>
/// These are Keycloak's own tokens, passed through unchanged. IslaPay does not
/// wrap, re-sign or re-issue them. Wrapping would mean a second signing key to
/// rotate, a second expiry to reason about, and a revocation that only works
/// if every service remembers to ask us — all of it duplicating something the
/// identity provider already does correctly.
/// </para>
/// <para>
/// <paramref name="ExpiresIn"/> is seconds, as OAuth 2 specifies, rather than
/// an absolute instant: the client's clock may be wrong, and a relative
/// lifetime is immune to that. The client should refresh before it elapses,
/// not after a 401.
/// </para>
/// </remarks>
/// <param name="AccessToken">Bearer token for the API. Short-lived.</param>
/// <param name="RefreshToken">
/// Exchanged for a new pair at <c>POST /v1/auth/token/refresh</c>. Must be
/// stored in the platform keystore, never in plain preferences.
/// </param>
/// <param name="ExpiresIn">Lifetime of the access token, in seconds.</param>
/// <param name="RefreshExpiresIn">Lifetime of the refresh token, in seconds.</param>
/// <param name="TokenType">Always <c>Bearer</c>.</param>
public sealed record TokenPair(
    string AccessToken,
    string RefreshToken,
    int ExpiresIn,
    int RefreshExpiresIn,
    string TokenType = "Bearer");

/// <summary>Who the tokens belong to.</summary>
/// <remarks>
/// Returned alongside the pair so the client can render a name without first
/// decoding a JWT. The client must not treat these as authoritative for
/// authorisation — the access token's claims are.
/// </remarks>
/// <param name="Id">Keycloak's subject (<c>sub</c>). Stable for the life of the account.</param>
/// <param name="PhoneVerified">
/// False until an OTP has been confirmed. Money movement is gated on this
/// server-side; the client uses it only to decide which screen to show.
/// </param>
/// <param name="Level">
/// 0 unverified, 1 phone proved, 2 identity checked by compliance. Decides the
/// most one movement may be; see <see cref="Standing"/>.
/// </param>
/// <param name="Frozen">
/// Compliance has stopped the account moving money. The client says so and
/// points at support instead of letting each screen fail on its own.
/// </param>
public sealed record UserDto(
    string Id,
    string Name,
    string Email,
    string? Phone,
    bool EmailVerified,
    bool PhoneVerified,
    int Level = 0,
    bool Frozen = false);

/// <summary>
/// An account as compliance and support see it.
/// </summary>
/// <param name="MaxPerMovement">
/// The most one movement may be at this level, in currency units, as a decimal
/// string like every other amount on the wire.
/// </param>
public sealed record AccountStandingDto(
    string UserId,
    string Name,
    string Email,
    string? Phone,
    bool PhoneVerified,
    bool IdentityVerified,
    int Level,
    string MaxPerMovement,
    bool Frozen,
    string? FrozenReason,
    DateTimeOffset? FrozenAt,
    string? FrozenBy);

/// <summary>Why an account is being frozen or unfrozen. Required, and kept.</summary>
public sealed record StandingChangeRequest(string Reason);

/// <summary>Sets an account's verification level: 1 or 2.</summary>
/// <remarks>
/// 0 is not settable: it means the phone is unproved, which is a fact about
/// the phone, not a decision. Level 2 needs the phone proved first.
/// </remarks>
public sealed record LevelChangeRequest(int Level, string Reason);

/// <summary>What <c>/v1/auth/login</c> and <c>/v1/auth/register</c> return.</summary>
public sealed record AuthSessionResponse(TokenPair Tokens, UserDto User);

/// <summary><c>POST /v1/auth/login</c>.</summary>
/// <remarks>
/// This is OAuth 2's Resource Owner Password Credentials grant in all but
/// name, and RFC 9700 asks implementers not to use it. IslaPay does, knowingly:
/// D8 of the contract keeps the branded native screens, and a native screen
/// that collects a password has no other way to reach the identity provider.
/// <para>
/// What that costs, written down so it is not rediscovered later: the app
/// handles the plaintext password, so it must never persist it or log it;
/// federated identity providers and any interactive step-up (WebAuthn, a
/// second factor prompt) cannot work through this grant. If either becomes a
/// requirement, this endpoint is replaced by authorization code with PKCE and
/// a system browser, and the screens go with it.
/// </para>
/// <para>
/// A second factor does work through it, as a field: the six digits from an
/// authenticator app, sent with the password. Staff need one; a customer who
/// has not set one up leaves it out.
/// </para>
/// </remarks>
/// <param name="Code">
/// The current code from the person's authenticator app, if they have one.
/// Without it such an account is refused exactly as a wrong password is.
/// </param>
public sealed record LoginRequest(string Email, string Password, string? Code = null);

/// <summary><c>POST /v1/auth/register</c>.</summary>
/// <param name="Phone">
/// E.164, including the country code. It is the OTP destination, so a wrong
/// one locks the account out of verification rather than merely looking untidy.
/// </param>
public sealed record RegisterRequest(
    string Name,
    string Email,
    string Phone,
    string Password);

/// <summary><c>POST /v1/auth/token/refresh</c>.</summary>
public sealed record RefreshRequest(string RefreshToken);

/// <summary><c>POST /v1/auth/otp/verify</c>.</summary>
/// <remarks>
/// On success the phone is marked verified and a <em>fresh</em> token pair is
/// returned. The old access token is still valid until it expires and still
/// says the phone is unverified, which is why verification is read from the
/// account rather than from a claim wherever it gates money.
/// </remarks>
public sealed record OtpVerifyRequest(string Phone, string Code);

/// <summary><c>POST /v1/auth/otp/resend</c>.</summary>
public sealed record OtpResendRequest(string Phone);

/// <summary>
/// What a resend returns.
/// </summary>
/// <remarks>
/// Deliberately says nothing about whether the phone belongs to an account.
/// Answering that turns this endpoint into an oracle for which numbers are
/// registered, and it is unauthenticated. <paramref name="RetryAfter"/> is the
/// only variable part, and it is the same for a known and an unknown number.
/// </remarks>
/// <param name="RetryAfter">Seconds before another resend is accepted.</param>
public sealed record OtpResendResponse(int RetryAfter);

/// <summary><c>POST /v1/auth/password/reset</c> — step one: ask for a code.</summary>
/// <remarks>
/// Like <see cref="OtpResendResponse"/>, the reply is identical whether or not
/// the address is registered.
/// </remarks>
public sealed record PasswordResetRequest(string Email);

/// <summary><c>POST /v1/auth/password/reset</c> — step two: the code and the new password.</summary>
public sealed record PasswordResetConfirmRequest(
    string Email,
    string Code,
    string NewPassword);
