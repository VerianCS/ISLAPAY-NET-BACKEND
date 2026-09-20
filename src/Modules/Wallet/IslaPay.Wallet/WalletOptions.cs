namespace IslaPay.Wallet;

/// <summary>Rules the Wallet module applies before money moves.</summary>
public sealed class WalletOptions
{
    /// <summary>
    /// Whether a proved phone number is required to move money (D11).
    /// </summary>
    /// <remarks>
    /// <para>
    /// True by default, because a payments account that never proved a way to
    /// reach its owner is the one an attacker wants. The check reads the
    /// account rather than a token claim: a token issued before verification
    /// keeps saying false until it expires, and one issued after says true for
    /// ever.
    /// </para>
    /// <para>
    /// It can be turned off, and right now it has to be anywhere without an SMS
    /// gateway — there is no adapter yet, so outside Development no code
    /// reaches a phone and nobody could ever pass the check. That is a hole,
    /// and it is deliberately a loud one: turning the rule off is a visible
    /// line of configuration rather than a silently missing check.
    /// </para>
    /// </remarks>
    public bool RequireVerifiedPhone { get; init; } = true;
}
