namespace IslaPay.Exchange.Contracts;

/// <summary>Error codes the Exchange module raises.</summary>
public static class ExchangeErrors
{
    /// <summary>
    /// The settlement fund cannot cover the destination leg. Client:
    /// <c>ExchangeFundUnavailable(currency)</c> — <c>meta.currency</c> required.
    /// </summary>
    public const string FundUnavailable = "fund_unavailable";

    /// <summary>
    /// The quote's <c>expiresAt</c> has passed. The client re-quotes and
    /// retries rather than surfacing this to the user.
    /// </summary>
    public const string QuoteExpired = "quote_expired";

    /// <summary>See <c>WalletErrors.RequireCurrencyMeta</c> for why this is per module.</summary>
    public static IReadOnlySet<string> RequireCurrencyMeta { get; } =
        new HashSet<string>(StringComparer.Ordinal) { FundUnavailable };
}
