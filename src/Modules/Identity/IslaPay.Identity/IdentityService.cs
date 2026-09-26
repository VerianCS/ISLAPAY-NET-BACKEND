using System.Buffers.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using IslaPay.Identity.Contracts;
using IslaPay.Platform.Messaging;

namespace IslaPay.Identity;

/// <summary>
/// The six identity flows, composed from Keycloak and our own OTP.
/// </summary>
/// <remarks>
/// <para>
/// The division of labour is fixed here and worth stating once: Keycloak owns
/// credentials, sessions, tokens and the password policy; IslaPay owns the
/// one-time codes and the rule that a phone must be proved before money moves.
/// Nothing in this class hashes a password, signs a token, or decides whether
/// a session is still valid.
/// </para>
/// <para>
/// Two endpoints answer identically whether or not an account exists —
/// <see cref="RequestPasswordResetAsync"/> and the resend path. They are
/// unauthenticated, and an endpoint that says "no such user" is a list of
/// every user.
/// </para>
/// </remarks>
public sealed partial class IdentityService
{
    private readonly ITokenClient _tokens;
    private readonly IAdminClient _admin;
    private readonly OtpService _otp;
    private readonly IOutbox _outbox;

    public IdentityService(ITokenClient tokens, IAdminClient admin, OtpService otp, IOutbox outbox)
    {
        ArgumentNullException.ThrowIfNull(tokens);
        ArgumentNullException.ThrowIfNull(admin);
        ArgumentNullException.ThrowIfNull(otp);
        ArgumentNullException.ThrowIfNull(outbox);
        _tokens = tokens;
        _admin = admin;
        _otp = otp;
        _outbox = outbox;
    }

    public async Task<AuthSessionResponse> LoginAsync(
        LoginRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var pair = await _tokens
            .PasswordGrantAsync(Normalise(request.Email), request.Password, request.Code, ct)
            .ConfigureAwait(false);

        var user = await _admin.GetAsync(SubjectOf(pair.AccessToken), ct).ConfigureAwait(false);
        return new AuthSessionResponse(pair, Describe(user));
    }

    /// <summary>
    /// Creates the account, sends the first code, and signs the user in.
    /// </summary>
    /// <remarks>
    /// The account is enabled immediately and the tokens are real. What the
    /// unverified phone withholds is money movement, not access — a user who
    /// cannot get past registration cannot read the terms, see their account,
    /// or ask for help, and blocking sign-in buys nothing that
    /// <c>phoneNumberVerified</c> does not already buy at the point it matters.
    /// </remarks>
    public async Task<AuthSessionResponse> RegisterAsync(
        RegisterRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var email = Normalise(request.Email);
        var phone = request.Phone?.Trim() ?? string.Empty;

        if (!E164().IsMatch(phone))
        {
            throw new IdentityException(
                IdentityErrors.InvalidPhone, 422,
                "The phone number must be in E.164 form, for example +5355123456.");
        }

        if (string.IsNullOrWhiteSpace(request.Name))
        {
            throw new IdentityException(
                IdentityErrors.NameRequired, 422, "A name is required.");
        }

        var userId = await _admin
            .CreateUserAsync(email, request.Name.Trim(), phone, request.Password, ct)
            .ConfigureAwait(false);

        // Announced through the outbox, in a transaction of its own.
        //
        // The weaker of the two guarantees the outbox offers, and the reason is
        // Keycloak: the account lives there, so there is no local transaction
        // for this write to join. If the process dies between the two, the user
        // exists and no event is emitted. Wallet closes that gap by opening
        // accounts on first read as well as on this event — the event is the
        // fast path, not the guarantee.
        await _outbox.EnqueueAsync(
            IdentityEvents.Context,
            IdentityEvents.UserRegistered,
            new UserRegistered(userId, email, phone, DateTimeOffset.UtcNow),
            correlationId: userId,
            cancellationToken: ct).ConfigureAwait(false);

        // Best effort. The account exists and the user can sign in; a failed
        // SMS is recoverable with a resend, and unwinding the account here
        // would leave the e-mail taken if the delete then failed too.
        try
        {
            await _otp.IssueAsync(OtpPurpose.PhoneVerification, phone, ct).ConfigureAwait(false);
        }
        catch (IdentityException)
        {
            // Deliberately ignored; the client's next screen offers a resend.
        }

        var pair = await _tokens
            .PasswordGrantAsync(email, request.Password, ct: ct)
            .ConfigureAwait(false);

        var user = await _admin.GetAsync(userId, ct).ConfigureAwait(false);
        return new AuthSessionResponse(pair, Describe(user));
    }

    public Task<TokenPair> RefreshAsync(RefreshRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (string.IsNullOrWhiteSpace(request.RefreshToken))
            throw IdentityException.TokenInvalid("No refresh token was sent.");

        return _tokens.RefreshAsync(request.RefreshToken, ct);
    }

    /// <summary>
    /// Confirms the phone of the <em>signed-in</em> user.
    /// </summary>
    /// <remarks>
    /// The account comes from the bearer token, and the phone in the body is
    /// only checked against it. Taking the phone from the body alone would
    /// make this an unauthenticated endpoint that grants a verified state to
    /// whoever holds a code — which is to say, to whoever can read one SMS.
    /// </remarks>
    public async Task<UserDto> VerifyOtpAsync(
        string userId, OtpVerifyRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var user = await _admin.GetAsync(userId, ct).ConfigureAwait(false);
        var phone = RequirePhone(user);

        if (!string.IsNullOrWhiteSpace(request.Phone)
            && !string.Equals(request.Phone.Trim(), phone, StringComparison.Ordinal))
        {
            // Not "wrong number": answering that would confirm what the real
            // number is not, one guess at a time.
            throw new IdentityException(
                IdentityErrors.OtpExpired, 422, "There is no code outstanding for that destination.");
        }

        await _otp.VerifyAsync(OtpPurpose.PhoneVerification, phone, request.Code, ct)
            .ConfigureAwait(false);

        await _admin.SetPhoneVerifiedAsync(userId, verified: true, ct).ConfigureAwait(false);

        return Describe(await _admin.GetAsync(userId, ct).ConfigureAwait(false));
    }

    /// <summary>Sends another phone code to the signed-in user's own number.</summary>
    public async Task<OtpResendResponse> ResendOtpAsync(string userId, CancellationToken ct = default)
    {
        var user = await _admin.GetAsync(userId, ct).ConfigureAwait(false);

        if (user.PhoneVerified)
        {
            // Nothing to prove. Sending anyway would be a way to make someone
            // pay for SMS on an account that is already done.
            return new OtpResendResponse(0);
        }

        var retryAfter = await _otp
            .IssueAsync(OtpPurpose.PhoneVerification, RequirePhone(user), ct)
            .ConfigureAwait(false);

        return new OtpResendResponse(retryAfter);
    }

    /// <summary>
    /// Step one of recovery: send a code to the address, if it has an account.
    /// </summary>
    /// <remarks>
    /// Returns the same thing either way, including when the address is not
    /// registered and nothing was sent. The cooldown is the only number in the
    /// response and it does not depend on the account, so the reply cannot be
    /// used to tell a registered address from an unregistered one.
    /// </remarks>
    public async Task<OtpResendResponse> RequestPasswordResetAsync(
        PasswordResetRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var email = Normalise(request.Email);
        var user = await _admin.FindByEmailAsync(email, ct).ConfigureAwait(false);

        // The same number the real path would return. A different one here is
        // precisely the difference an attacker measures.
        var cooldown = _otp.ResendCooldownSeconds;
        if (user is null) return new OtpResendResponse(cooldown);

        try
        {
            cooldown = await _otp
                .IssueAsync(OtpPurpose.PasswordReset, email, ct)
                .ConfigureAwait(false);
        }
        catch (IdentityException e) when (e.Code == IdentityErrors.OtpRateLimited)
        {
            // Surfaced as a normal reply with the wait, because the alternative
            // — a 429 for a known address and a 200 for an unknown one — is the
            // enumeration this endpoint exists to avoid.
            cooldown = e.Meta.TryGetValue("retryAfter", out var wait) && wait is int seconds
                ? seconds
                : cooldown;
        }

        return new OtpResendResponse(cooldown);
    }

    /// <summary>Step two: the code, and the new password.</summary>
    public async Task ConfirmPasswordResetAsync(
        PasswordResetConfirmRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var email = Normalise(request.Email);

        // Verified before the account is looked up, so an unknown address and
        // a wrong code fail identically — same code, same status, same timing
        // characteristics, since no code was ever issued for an address with
        // no account.
        await _otp.VerifyAsync(OtpPurpose.PasswordReset, email, request.Code, ct)
            .ConfigureAwait(false);

        var user = await _admin.FindByEmailAsync(email, ct).ConfigureAwait(false)
            ?? throw new IdentityException(
                IdentityErrors.OtpExpired, 422, "There is no code outstanding for that destination.");

        await _admin.SetPasswordAsync(user.Id, request.NewPassword, ct).ConfigureAwait(false);
    }

    public Task LogoutAsync(string refreshToken, CancellationToken ct = default) =>
        _tokens.LogoutAsync(refreshToken, ct);

    // ------------------------------------------------------------------ helpers

    private static UserDto Describe(KeycloakUser user) => new(
        Id: user.Id,
        Name: user.DisplayName,
        Email: user.Email,
        Phone: user.Phone,
        EmailVerified: user.EmailVerified,
        PhoneVerified: user.PhoneVerified);

    private static string RequirePhone(KeycloakUser user) =>
        user.Phone is { Length: > 0 } phone
            ? phone
            : throw new IdentityException(
                IdentityErrors.InvalidPhone, 422, "The account has no phone number on file.");

    /// <summary>Lower-cased and trimmed, because Keycloak stores it that way.</summary>
    private static string Normalise(string? email) =>
        (email ?? string.Empty).Trim().ToLowerInvariant();

    /// <summary>
    /// Reads <c>sub</c> from an access token without validating it.
    /// </summary>
    /// <remarks>
    /// Safe precisely here and nowhere else: this token arrived in the body of
    /// the response to our own request to the provider, over the connection we
    /// opened to it. It has not crossed the network from a caller. Any token
    /// that <em>has</em> is validated by the API's bearer middleware against
    /// the realm's JWKS before it reaches this class.
    /// </remarks>
    internal static string SubjectOf(string accessToken)
    {
        var parts = accessToken.Split('.');
        if (parts.Length < 2)
            throw IdentityException.Unavailable("The provider returned a token we cannot read.");

        try
        {
            using var payload = JsonDocument.Parse(Base64UrlDecode(parts[1]));
            return payload.RootElement.TryGetProperty("sub", out var sub)
                && sub.GetString() is { Length: > 0 } subject
                ? subject
                : throw IdentityException.Unavailable("The provider's token carried no subject.");
        }
        catch (Exception e) when (e is JsonException or FormatException)
        {
            throw IdentityException.Unavailable("The provider's token could not be decoded.", e);
        }
    }

    private static byte[] Base64UrlDecode(string value)
    {
        Span<byte> buffer = new byte[Base64Url.GetMaxDecodedLength(value.Length)];
        return Base64Url.TryDecodeFromChars(value, buffer, out var written)
            ? buffer[..written].ToArray()
            : throw new FormatException("Not base64url.");
    }

    /// <summary>
    /// E.164: a plus, a non-zero country digit, then up to fourteen more.
    /// </summary>
    /// <remarks>
    /// Deliberately only the shape. Whether +5355123456 is a number that rings
    /// is not something a regular expression knows, and the OTP is what
    /// actually answers that question.
    /// </remarks>
    [GeneratedRegex(@"^\+[1-9]\d{6,14}$")]
    private static partial Regex E164();
}
