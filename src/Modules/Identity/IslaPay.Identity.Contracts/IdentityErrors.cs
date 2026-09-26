namespace IslaPay.Identity.Contracts;

/// <summary>Error codes the Identity module raises.</summary>
/// <remarks>
/// Owned by this module, not by a shared catalogue, so that extracting
/// Identity into its own service later is a move rather than an untangling.
/// Codes the platform raises before a module is reached live in
/// <c>PlatformErrors</c>.
/// </remarks>
public static class IdentityErrors
{
    /// <summary>
    /// Wrong e-mail or password. Deliberately one code for both: telling the
    /// caller which half was wrong turns the login endpoint into a way to
    /// enumerate registered addresses.
    /// </summary>
    public const string InvalidCredentials = "invalid_credentials";

    /// <summary>
    /// The account exists and the password is right, but the account is
    /// disabled. Distinct from <see cref="InvalidCredentials"/> because the
    /// user can act on it, and it is only ever returned to someone who has
    /// already proved they own the account.
    /// </summary>
    public const string AccountDisabled = "account_disabled";

    /// <summary>Registration with an address that already has an account.</summary>
    public const string EmailTaken = "email_taken";

    /// <summary>
    /// The identity provider's password policy rejected it. <c>detail</c>
    /// carries the provider's reason; the client shows its own copy.
    /// </summary>
    public const string WeakPassword = "weak_password";

    /// <summary>Not a phone number in E.164 form.</summary>
    public const string InvalidPhone = "invalid_phone";

    /// <summary>
    /// Registration without a name. Distinct from the card-holder name: this
    /// one names the account, not a piece of plastic.
    /// </summary>
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
    /// The identity provider's brute-force protection has locked this account
    /// out for now. Honour <c>Retry-After</c>; a faster retry extends it.
    /// </summary>
    public const string TooManyAttempts = "too_many_attempts";

    /// <summary>
    /// The identity provider could not be reached or answered unusably. A
    /// dependency failure, not the caller's fault, and safe to retry.
    /// </summary>
    public const string IdentityUnavailable = "identity_unavailable";

    /// <summary>No account has that id or address. Staff routes only.</summary>
    public const string AccountNotFound = "account_not_found";

    /// <summary>A freeze, unfreeze or level change without a reason, or a level that is not 1 or 2.</summary>
    public const string InvalidStandingChange = "invalid_standing_change";
}
