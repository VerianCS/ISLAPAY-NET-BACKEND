namespace IslaPay.Custody.Contracts;

/// <summary>
/// This module's codes. See <c>API_CONTRACT.md</c> §4.
/// </summary>
public static class CustodyErrors
{
    /// <summary>A network this build does not watch. 404.</summary>
    public const string UnknownNetwork = "unknown_network";

    /// <summary>
    /// That asset is not on that chain, or the pair is switched off. 422.
    /// </summary>
    /// <remarks>
    /// Its own code rather than a validation error, because the request is
    /// well-formed and the combination is the mistake. USDT is on TRON and on
    /// Ethereum; USDC is not on TRON at all. Which pairs exist is a row in
    /// <c>catalog.currency_networks</c>, not something this module decides.
    /// </remarks>
    public const string CurrencyNotOnNetwork = "currency_not_on_network";

    /// <summary>
    /// No address could be issued. 503.
    /// </summary>
    /// <remarks>
    /// The custodian is unreachable, or it answered with something that is not
    /// a valid address for the network. Either way it is temporary and not the
    /// caller's fault, and the one thing that must never happen is showing
    /// somebody an address that was not checked.
    /// </remarks>
    public const string AddressUnavailable = "address_unavailable";

    /// <summary>Money cannot move until the account's phone is proved. 403.</summary>
    public const string PhoneNotVerified = "phone_not_verified";
}
