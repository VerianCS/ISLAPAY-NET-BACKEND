using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace IslaPay.Identity;

/// <summary>
/// Challenges in process memory.
/// </summary>
/// <remarks>
/// Correct for a single instance and wrong for more than one: a code issued by
/// the instance that took the resend will not be found by the instance that
/// takes the verify, and the user sees "there is no code outstanding" for a
/// code they are holding. Before the API runs behind more than one replica
/// this is replaced by a shared store — Redis with a TTL is the obvious fit,
/// since expiry is exactly what it does well.
/// <para>
/// Expired entries are swept on access rather than by a timer. A challenge
/// nobody asks about costs a few dozen bytes for five minutes.
/// </para>
/// </remarks>
public sealed class InMemoryOtpStore : IOtpStore
{
    private readonly ConcurrentDictionary<(OtpPurpose, string), OtpChallenge> _challenges = new();
    private readonly TimeProvider _clock;

    public InMemoryOtpStore(TimeProvider? clock = null) => _clock = clock ?? TimeProvider.System;

    public Task<OtpChallenge?> GetAsync(
        OtpPurpose purpose, string destination, CancellationToken ct = default)
    {
        if (!_challenges.TryGetValue((purpose, destination), out var challenge))
            return Task.FromResult<OtpChallenge?>(null);

        if (_clock.GetUtcNow() >= challenge.ExpiresAt)
        {
            // Returned rather than hidden: the caller distinguishes "expired"
            // from "never existed" in the message it shows, and silently
            // dropping it here would make the two indistinguishable.
            _challenges.TryRemove((purpose, destination), out _);
            return Task.FromResult<OtpChallenge?>(challenge);
        }

        return Task.FromResult<OtpChallenge?>(challenge);
    }

    public Task SaveAsync(OtpChallenge challenge, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(challenge);
        _challenges[(challenge.Purpose, challenge.Destination)] = challenge;
        return Task.CompletedTask;
    }

    public Task RemoveAsync(OtpPurpose purpose, string destination, CancellationToken ct = default)
    {
        _challenges.TryRemove((purpose, destination), out _);
        return Task.CompletedTask;
    }
}

/// <summary>
/// Writes the code to the log instead of sending it.
/// </summary>
/// <remarks>
/// For local development only, and it says so when it starts. Logging a
/// one-time code defeats the point of it, so this must never be registered in
/// an environment whose logs are shipped anywhere. The registration in
/// <c>Program.cs</c> refuses to use it outside Development for that reason.
/// </remarks>
public sealed partial class DevelopmentOtpSender : IOtpSender
{
    private readonly ILogger<DevelopmentOtpSender> _log;

    public DevelopmentOtpSender(ILogger<DevelopmentOtpSender> log)
    {
        ArgumentNullException.ThrowIfNull(log);
        _log = log;
        Banner(_log);
    }

    public Task SendAsync(
        OtpPurpose purpose, string destination, string code, CancellationToken ct = default)
    {
        Code(_log, purpose, destination, code);
        return Task.CompletedTask;
    }

    [LoggerMessage(
        EventId = 1,
        Level = LogLevel.Warning,
        Message = "One-time codes are being written to the log. Development only.")]
    private static partial void Banner(ILogger logger);

    [LoggerMessage(
        EventId = 2,
        Level = LogLevel.Information,
        Message = "OTP for {purpose} to {destination}: {code}")]
    private static partial void Code(
        ILogger logger, OtpPurpose purpose, string destination, string code);
}
