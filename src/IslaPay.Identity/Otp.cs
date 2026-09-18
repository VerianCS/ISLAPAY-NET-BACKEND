using System.Security.Cryptography;
using System.Text;
using IslaPay.Contracts;

namespace IslaPay.Identity;

/// <summary>What a code is for. Codes are never valid across purposes.</summary>
/// <remarks>
/// Separating these is not tidiness. A code sent to confirm a phone number and
/// a code that authorises a password reset have different consequences, and if
/// one challenge could satisfy the other, the weaker flow would decide the
/// stronger one's security.
/// </remarks>
public enum OtpPurpose
{
    PhoneVerification,
    PasswordReset,
}

/// <summary>Timings and limits for one-time codes.</summary>
public sealed class OtpOptions
{
    /// <summary>
    /// Six digits, matching the client's existing screens. A million
    /// possibilities is only safe because <see cref="MaxAttempts"/> and
    /// <see cref="Lifetime"/> are small; lengthening one means revisiting the
    /// others, not relaxing them.
    /// </summary>
    public int Digits { get; init; } = 6;

    public TimeSpan Lifetime { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>Wrong guesses before the challenge is destroyed.</summary>
    public int MaxAttempts { get; init; } = 5;

    /// <summary>Minimum interval between sends to the same destination.</summary>
    public TimeSpan ResendCooldown { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>
    /// A server-held key mixed into every stored hash.
    /// </summary>
    /// <remarks>
    /// Without it, a dump of the challenge store is a dump of the codes: six
    /// digits and a known salt fall to a million hashes, which is no work at
    /// all. With it, the store is useless on its own. It comes from the secret
    /// store; the default below exists so tests and local runs work, and a
    /// deployment that leaves it at the default is misconfigured.
    /// </remarks>
    public string PepperKey { get; init; } = "development-only-otp-pepper";
}

/// <summary>An outstanding challenge. Holds a hash, never the code.</summary>
public sealed record OtpChallenge(
    OtpPurpose Purpose,
    string Destination,
    byte[] Hash,
    byte[] Salt,
    DateTimeOffset IssuedAt,
    DateTimeOffset ExpiresAt,
    int AttemptsLeft);

/// <summary>Where outstanding challenges live.</summary>
/// <remarks>
/// One challenge per (purpose, destination): issuing a new code invalidates
/// the previous one. Keeping several alive would multiply an attacker's
/// chances by the number of resends they can trigger.
/// </remarks>
public interface IOtpStore
{
    Task<OtpChallenge?> GetAsync(OtpPurpose purpose, string destination, CancellationToken ct = default);

    Task SaveAsync(OtpChallenge challenge, CancellationToken ct = default);

    Task RemoveAsync(OtpPurpose purpose, string destination, CancellationToken ct = default);
}

/// <summary>Delivers a code to its destination.</summary>
/// <remarks>
/// A port, not an implementation, because the SMS provider for Cuba is not
/// decided and because tests need to read the code without an SMS gateway.
/// </remarks>
public interface IOtpSender
{
    Task SendAsync(OtpPurpose purpose, string destination, string code, CancellationToken ct = default);
}

/// <summary>
/// Issues and checks one-time codes.
/// </summary>
/// <remarks>
/// Keycloak is not asked to do this. Its OTP support is TOTP for second-factor
/// sign-in, which is a different thing from proving that a phone number
/// reaches the person registering: there is no shared secret yet, and the code
/// has to travel by SMS. So IslaPay owns the challenge, and Keycloak owns the
/// resulting fact — <c>phoneNumberVerified</c> on the account.
/// </remarks>
public sealed class OtpService
{
    private readonly IOtpStore _store;
    private readonly IOtpSender _sender;
    private readonly OtpOptions _options;
    private readonly TimeProvider _clock;

    public OtpService(
        IOtpStore store, IOtpSender sender, OtpOptions options, TimeProvider? clock = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(sender);
        ArgumentNullException.ThrowIfNull(options);
        _store = store;
        _sender = sender;
        _options = options;
        _clock = clock ?? TimeProvider.System;
    }

    /// <summary>
    /// The configured wait between sends, in seconds.
    /// </summary>
    /// <remarks>
    /// Read by callers that have to answer with a cooldown for a destination
    /// they did not actually send to. If they made one up, the made-up number
    /// and the real one would differ, and the difference would say whether the
    /// address has an account.
    /// </remarks>
    public int ResendCooldownSeconds => (int)_options.ResendCooldown.TotalSeconds;

    /// <summary>
    /// Issues a code and sends it, replacing any outstanding one.
    /// </summary>
    /// <returns>Seconds the caller must wait before asking again.</returns>
    /// <exception cref="IdentityException">
    /// <see cref="ErrorCodes.OtpRateLimited"/> if the cooldown has not elapsed.
    /// </exception>
    public async Task<int> IssueAsync(
        OtpPurpose purpose, string destination, CancellationToken ct = default)
    {
        var now = _clock.GetUtcNow();

        var existing = await _store.GetAsync(purpose, destination, ct).ConfigureAwait(false);
        if (existing is not null)
        {
            var elapsed = now - existing.IssuedAt;
            if (elapsed < _options.ResendCooldown)
            {
                var wait = (int)Math.Ceiling((_options.ResendCooldown - elapsed).TotalSeconds);
                var rateLimited = new IdentityException(
                    ErrorCodes.OtpRateLimited, 429, "A code was sent recently.");
                rateLimited.Meta["retryAfter"] = wait;
                throw rateLimited;
            }
        }

        var code = NewCode(_options.Digits);
        var salt = RandomNumberGenerator.GetBytes(16);

        await _store.SaveAsync(
            new OtpChallenge(
                purpose,
                destination,
                Hash(code, salt, _options.PepperKey),
                salt,
                IssuedAt: now,
                ExpiresAt: now + _options.Lifetime,
                AttemptsLeft: _options.MaxAttempts),
            ct).ConfigureAwait(false);

        // Sent after the challenge is stored. The other order can deliver a
        // code that the store does not know about, and the user then types a
        // correct code that is rejected.
        await _sender.SendAsync(purpose, destination, code, ct).ConfigureAwait(false);

        return (int)_options.ResendCooldown.TotalSeconds;
    }

    /// <summary>
    /// Checks a code and consumes the challenge.
    /// </summary>
    /// <remarks>
    /// Single use, whatever the outcome that ends it: a correct code is
    /// consumed on success, and a challenge that runs out of attempts or ages
    /// out is destroyed rather than left to be guessed at again.
    /// </remarks>
    /// <exception cref="IdentityException">
    /// <see cref="ErrorCodes.OtpExpired"/>, <see cref="ErrorCodes.OtpInvalid"/>
    /// or <see cref="ErrorCodes.OtpLocked"/>.
    /// </exception>
    public async Task VerifyAsync(
        OtpPurpose purpose, string destination, string code, CancellationToken ct = default)
    {
        var challenge = await _store.GetAsync(purpose, destination, ct).ConfigureAwait(false);

        if (challenge is null)
        {
            throw new IdentityException(
                ErrorCodes.OtpExpired, 422, "There is no code outstanding for that destination.");
        }

        var now = _clock.GetUtcNow();
        if (now >= challenge.ExpiresAt)
        {
            await _store.RemoveAsync(purpose, destination, ct).ConfigureAwait(false);
            throw new IdentityException(ErrorCodes.OtpExpired, 422, "The code has expired.");
        }

        var candidate = Hash(code ?? string.Empty, challenge.Salt, _options.PepperKey);

        // Fixed-time, so the number of leading digits that matched cannot be
        // read off the response time. Six digits guessed one position at a
        // time is sixty tries, not a million.
        if (CryptographicOperations.FixedTimeEquals(candidate, challenge.Hash))
        {
            await _store.RemoveAsync(purpose, destination, ct).ConfigureAwait(false);
            return;
        }

        var left = challenge.AttemptsLeft - 1;
        if (left <= 0)
        {
            await _store.RemoveAsync(purpose, destination, ct).ConfigureAwait(false);
            throw new IdentityException(
                ErrorCodes.OtpLocked, 429, "Too many wrong codes. Request a new one.");
        }

        await _store.SaveAsync(challenge with { AttemptsLeft = left }, ct).ConfigureAwait(false);

        var wrong = new IdentityException(ErrorCodes.OtpInvalid, 422, "Wrong code.");
        wrong.Meta["attemptsLeft"] = left;
        throw wrong;
    }

    /// <summary>
    /// A uniformly distributed decimal code.
    /// </summary>
    /// <remarks>
    /// <see cref="RandomNumberGenerator"/>, not <see cref="Random"/>: a code
    /// generated from a predictable sequence is not a secret, and
    /// <c>Random</c> seeded from the clock is exactly that.
    /// <para>
    /// Built digit by digit with <c>GetInt32</c>, which is unbiased. Taking a
    /// large random number modulo a power of ten is not, and the bias shows up
    /// in the leading digit.
    /// </para>
    /// </remarks>
    internal static string NewCode(int digits)
    {
        var builder = new StringBuilder(digits);
        for (var i = 0; i < digits; i++)
            builder.Append((char)('0' + RandomNumberGenerator.GetInt32(10)));
        return builder.ToString();
    }

    private static byte[] Hash(string code, byte[] salt, string pepper)
    {
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(pepper));
        var message = new byte[salt.Length + Encoding.UTF8.GetByteCount(code)];
        salt.CopyTo(message, 0);
        Encoding.UTF8.GetBytes(code, message.AsSpan(salt.Length));
        return hmac.ComputeHash(message);
    }
}
