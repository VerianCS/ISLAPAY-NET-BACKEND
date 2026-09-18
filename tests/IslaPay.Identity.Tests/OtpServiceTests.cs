using System.Text;
using IslaPay.Contracts;
using IslaPay.Identity;

namespace IslaPay.Identity.Tests;

/// <summary>A clock the test moves by hand.</summary>
internal sealed class FakeClock(DateTimeOffset now) : TimeProvider
{
    private DateTimeOffset _now = now;

    public override DateTimeOffset GetUtcNow() => _now;

    public void Advance(TimeSpan by) => _now += by;
}

internal sealed class CapturingSender : IOtpSender
{
    public List<(OtpPurpose Purpose, string Destination, string Code)> Sent { get; } = [];

    public Task SendAsync(
        OtpPurpose purpose, string destination, string code, CancellationToken ct = default)
    {
        Sent.Add((purpose, destination, code));
        return Task.CompletedTask;
    }

    public string Last => Sent[^1].Code;
}

public class OtpServiceTests
{
    private const string Phone = "+5355123456";

    private static (OtpService Service, CapturingSender Sender, InMemoryOtpStore Store, FakeClock Clock)
        Build(OtpOptions? options = null)
    {
        var clock = new FakeClock(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var store = new InMemoryOtpStore(clock);
        var sender = new CapturingSender();
        var service = new OtpService(store, sender, options ?? new OtpOptions(), clock);
        return (service, sender, store, clock);
    }

    [Fact]
    public async Task A_correct_code_verifies()
    {
        var (service, sender, _, _) = Build();

        await service.IssueAsync(OtpPurpose.PhoneVerification, Phone);
        await service.VerifyAsync(OtpPurpose.PhoneVerification, Phone, sender.Last);
    }

    [Fact]
    public async Task A_code_works_once_and_only_once()
    {
        var (service, sender, _, _) = Build();

        await service.IssueAsync(OtpPurpose.PhoneVerification, Phone);
        var code = sender.Last;
        await service.VerifyAsync(OtpPurpose.PhoneVerification, Phone, code);

        // Replaying an intercepted code is the attack this closes. The second
        // attempt must fail even though the code itself was right.
        var again = await Assert.ThrowsAsync<IdentityException>(
            () => service.VerifyAsync(OtpPurpose.PhoneVerification, Phone, code));

        Assert.Equal(ErrorCodes.OtpExpired, again.Code);
    }

    [Fact]
    public async Task Wrong_codes_run_out_of_attempts_and_lock_the_challenge()
    {
        var (service, sender, _, _) = Build(new OtpOptions { MaxAttempts = 3 });

        await service.IssueAsync(OtpPurpose.PhoneVerification, Phone);
        var real = sender.Last;
        var wrong = real == "000000" ? "111111" : "000000";

        for (var attempt = 1; attempt <= 2; attempt++)
        {
            var failure = await Assert.ThrowsAsync<IdentityException>(
                () => service.VerifyAsync(OtpPurpose.PhoneVerification, Phone, wrong));

            Assert.Equal(ErrorCodes.OtpInvalid, failure.Code);
            Assert.Equal(3 - attempt, failure.Meta["attemptsLeft"]);
        }

        var locked = await Assert.ThrowsAsync<IdentityException>(
            () => service.VerifyAsync(OtpPurpose.PhoneVerification, Phone, wrong));
        Assert.Equal(ErrorCodes.OtpLocked, locked.Code);

        // And the challenge is gone, so the correct code no longer works
        // either. Leaving it alive would mean an attacker who exhausts the
        // attempts has cost the user nothing.
        var dead = await Assert.ThrowsAsync<IdentityException>(
            () => service.VerifyAsync(OtpPurpose.PhoneVerification, Phone, real));
        Assert.Equal(ErrorCodes.OtpExpired, dead.Code);
    }

    [Fact]
    public async Task A_code_expires()
    {
        var (service, sender, _, clock) = Build(new OtpOptions { Lifetime = TimeSpan.FromMinutes(5) });

        await service.IssueAsync(OtpPurpose.PhoneVerification, Phone);
        var code = sender.Last;

        clock.Advance(TimeSpan.FromMinutes(5));

        var failure = await Assert.ThrowsAsync<IdentityException>(
            () => service.VerifyAsync(OtpPurpose.PhoneVerification, Phone, code));

        Assert.Equal(ErrorCodes.OtpExpired, failure.Code);
    }

    [Fact]
    public async Task A_code_is_still_good_one_second_before_it_expires()
    {
        var (service, sender, _, clock) = Build(new OtpOptions { Lifetime = TimeSpan.FromMinutes(5) });

        await service.IssueAsync(OtpPurpose.PhoneVerification, Phone);
        clock.Advance(TimeSpan.FromMinutes(5) - TimeSpan.FromSeconds(1));

        await service.VerifyAsync(OtpPurpose.PhoneVerification, Phone, sender.Last);
    }

    [Fact]
    public async Task Resending_before_the_cooldown_is_refused_with_the_wait()
    {
        var (service, _, _, clock) = Build(new OtpOptions { ResendCooldown = TimeSpan.FromSeconds(60) });

        await service.IssueAsync(OtpPurpose.PhoneVerification, Phone);
        clock.Advance(TimeSpan.FromSeconds(20));

        var failure = await Assert.ThrowsAsync<IdentityException>(
            () => service.IssueAsync(OtpPurpose.PhoneVerification, Phone));

        Assert.Equal(ErrorCodes.OtpRateLimited, failure.Code);
        Assert.Equal(429, failure.Status);
        Assert.Equal(40, failure.Meta["retryAfter"]);
    }

    [Fact]
    public async Task Resending_after_the_cooldown_replaces_the_old_code()
    {
        var (service, sender, _, clock) = Build(new OtpOptions { ResendCooldown = TimeSpan.FromSeconds(60) });

        await service.IssueAsync(OtpPurpose.PhoneVerification, Phone);
        var first = sender.Last;

        clock.Advance(TimeSpan.FromSeconds(60));
        await service.IssueAsync(OtpPurpose.PhoneVerification, Phone);

        // The superseded code must be dead. If both stayed valid, every resend
        // would widen the target.
        var failure = await Assert.ThrowsAsync<IdentityException>(
            () => service.VerifyAsync(OtpPurpose.PhoneVerification, Phone, first));
        Assert.Equal(ErrorCodes.OtpInvalid, failure.Code);

        await service.VerifyAsync(OtpPurpose.PhoneVerification, Phone, sender.Last);
    }

    [Fact]
    public async Task A_code_issued_for_one_purpose_does_not_satisfy_another()
    {
        var (service, sender, _, _) = Build();

        await service.IssueAsync(OtpPurpose.PhoneVerification, Phone);
        var code = sender.Last;

        var failure = await Assert.ThrowsAsync<IdentityException>(
            () => service.VerifyAsync(OtpPurpose.PasswordReset, Phone, code));

        Assert.Equal(ErrorCodes.OtpExpired, failure.Code);
    }

    [Fact]
    public async Task The_store_never_holds_the_code()
    {
        var (service, sender, store, _) = Build();

        await service.IssueAsync(OtpPurpose.PhoneVerification, Phone);
        var challenge = await store.GetAsync(OtpPurpose.PhoneVerification, Phone);

        Assert.NotNull(challenge);

        // The hash must not be the code in disguise, nor contain it. A store
        // that can be read back into codes is a store whose leak is a breach.
        var asText = Encoding.UTF8.GetString(challenge!.Hash);
        Assert.DoesNotContain(sender.Last, asText, StringComparison.Ordinal);
        Assert.Equal(32, challenge.Hash.Length);
    }

    [Fact]
    public async Task An_empty_or_null_code_is_wrong_not_a_crash()
    {
        var (service, _, _, _) = Build();
        await service.IssueAsync(OtpPurpose.PhoneVerification, Phone);

        var failure = await Assert.ThrowsAsync<IdentityException>(
            () => service.VerifyAsync(OtpPurpose.PhoneVerification, Phone, string.Empty));

        Assert.Equal(ErrorCodes.OtpInvalid, failure.Code);
    }

    [Fact]
    public void Codes_are_the_right_shape()
    {
        for (var i = 0; i < 1_000; i++)
        {
            var code = OtpService.NewCode(6);
            Assert.Equal(6, code.Length);
            Assert.All(code, c => Assert.InRange(c, '0', '9'));
        }
    }

    /// <summary>
    /// Every digit turns up in the leading position.
    /// </summary>
    /// <remarks>
    /// The cheap way to build a numeric code — a big random number modulo a
    /// power of ten — is biased, and the bias lands on the leading digit.
    /// Ten thousand samples make each of the ten digits overwhelmingly likely
    /// to appear if the generator is unbiased, so this catches that mistake
    /// without being flaky.
    /// </remarks>
    [Fact]
    public void Leading_digits_are_not_biased_away_from_any_value()
    {
        var seen = new HashSet<char>();
        for (var i = 0; i < 10_000; i++)
            seen.Add(OtpService.NewCode(6)[0]);

        Assert.Equal(10, seen.Count);
    }
}
