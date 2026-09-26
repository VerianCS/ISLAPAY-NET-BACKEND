using IslaPay.Platform;

namespace IslaPay.Identity.Contracts;

/// <summary>
/// Whether an account may move money, and how much at once.
/// </summary>
/// <remarks>
/// <para>
/// One rule, read by every module that moves a customer's money — Wallet,
/// P2P, Marketplace, Custody — so a freeze or a limit means the same thing
/// wherever the person tries. Each module turns a refusal into its own
/// exception with the same code, as it already does for the phone.
/// </para>
/// <para>
/// The limits are per movement, in units of the currency. Every currency a
/// customer can hold today (E-ISLA, USDT, USDC) is worth a dollar, which is
/// what makes one number serve all three; a holdable currency that is not
/// would need a rate here first. Daily and monthly totals are not counted
/// yet — that needs the ledger to sum a person's outflows, and is the next
/// step, not this one.
/// </para>
/// </remarks>
public static class Standing
{
    public const int Unverified = 0;
    public const int PhoneVerified = 1;
    public const int IdentityVerified = 2;

    /// <summary>The account is frozen by compliance.</summary>
    public const string AccountFrozen = "account_frozen";

    /// <summary>
    /// The amount is more than the account's level allows in one movement.
    /// <c>meta</c> carries <c>limit</c>, <c>currency</c> and <c>level</c>.
    /// </summary>
    public const string LimitExceeded = "limit_exceeded";

    /// <summary>The most one movement may be at each level, in currency units.</summary>
    public static decimal MaxPerMovement(int level) => level switch
    {
        <= Unverified => 0m,
        PhoneVerified => 1_000m,
        _ => 25_000m,
    };

    /// <summary>
    /// Why <paramref name="user"/> may not move <paramref name="amount"/>, or
    /// null if they may.
    /// </summary>
    /// <remarks>
    /// The phone is not checked here: each module already refuses an
    /// unverified phone with its own switch, and tests turn it off.
    /// </remarks>
    public static StandingRefusal? Check(DirectoryUser user, Money? amount = null)
    {
        ArgumentNullException.ThrowIfNull(user);

        if (user.Frozen)
        {
            return new StandingRefusal(
                AccountFrozen, 403,
                "This account cannot move money at the moment. Contact support.",
                new Dictionary<string, object>(StringComparer.Ordinal));
        }

        if (amount is { } money && user.PhoneVerified)
        {
            var limit = MaxPerMovement(user.Level);
            if (money.ToDecimal() > limit)
            {
                return new StandingRefusal(
                    LimitExceeded, 422,
                    $"At this verification level one movement can be at most {limit} {money.Currency.Code}.",
                    new Dictionary<string, object>(StringComparer.Ordinal)
                    {
                        ["limit"] = limit.ToString(System.Globalization.CultureInfo.InvariantCulture),
                        ["currency"] = money.Currency.Code,
                        ["level"] = user.Level,
                    });
            }
        }

        return null;
    }
}

/// <summary>A reason not to move money, for a module to raise as its own failure.</summary>
public sealed record StandingRefusal(
    string Code, int Status, string Message, IReadOnlyDictionary<string, object> Facts);
