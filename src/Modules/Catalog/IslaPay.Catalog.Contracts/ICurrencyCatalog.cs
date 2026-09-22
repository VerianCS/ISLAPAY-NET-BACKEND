using IslaPay.Platform;
using IslaPay.Platform.Api;

namespace IslaPay.Catalog.Contracts;

/// <summary>
/// Which currencies and chains exist, and what they are.
/// </summary>
/// <remarks>
/// <para>
/// The replacement for a four-member enum. Everything here comes from
/// <c>catalog.currencies</c> and its companions, which means a currency can be
/// added, renamed, or switched off without a deploy — the thing the enum made
/// impossible.
/// </para>
/// <para>
/// <b>Synchronous on purpose.</b> The catalogue is small, changes rarely, and
/// is read on nearly every code path that touches money; an <c>await</c> on
/// every currency lookup would put a database round trip inside arithmetic.
/// It is loaded once into an immutable snapshot and replaced whole by
/// <see cref="RefreshAsync"/>, so a reader never sees a half-changed
/// catalogue and never blocks on one.
/// </para>
/// </remarks>
public interface ICurrencyCatalog : ICurrencyScales
{
    /// <summary>
    /// The currency for a code, or a refusal naming it.
    /// </summary>
    /// <remarks>
    /// Throws for a code that is unknown <i>or</i> switched off, and the two
    /// are deliberately the same answer to a caller: a currency that is off is
    /// one no amount may be denominated in, and letting one through because it
    /// used to work is how a disabled jurisdiction keeps trading.
    /// </remarks>
    Currency Require(string code);

    bool TryFind(string? code, out Currency currency);

    /// <summary>
    /// Everything known about a code, switched on or not.
    /// </summary>
    /// <remarks>
    /// Unlike <see cref="Require"/> this answers for a disabled currency,
    /// because reading an old posting denominated in one has to keep working.
    /// History does not stop being true when a currency is withdrawn.
    /// </remarks>
    CurrencyInfo? Describe(string? code);

    /// <summary>Every currency, in display order. Includes the disabled.</summary>
    IReadOnlyList<CurrencyInfo> Currencies { get; }

    /// <summary>
    /// The ones a customer may hold a balance in, switched on.
    /// </summary>
    /// <remarks>
    /// What Wallet opens accounts in. It used to be a hard-coded list of
    /// three, which meant enabling a fourth currency for customers was a
    /// deploy.
    /// </remarks>
    IReadOnlyList<CurrencyInfo> Holdable { get; }

    IReadOnlyList<NetworkInfo> Networks { get; }

    /// <summary>The chains one asset actually exists on.</summary>
    IReadOnlyList<CurrencyOnNetwork> NetworksFor(string? currencyCode);

    /// <summary>One asset on one chain, or null.</summary>
    CurrencyOnNetwork? OnNetwork(string? currencyCode, string? networkId);

    /// <summary>Re-reads the tables and swaps the snapshot in one go.</summary>
    Task RefreshAsync(CancellationToken cancellationToken = default);
}

/// <summary>Codes this build names in code, because it has to.</summary>
/// <remarks>
/// <para>
/// A short list, and every entry earns its place: Wallet knows which unit is
/// IslaPay's own, P2P knows the peso leg is a platform liability, Custody
/// knows a TRON deposit address is for a token and not for TRX. Naming a code
/// is fine. Naming a <i>scale</i> is not — that is the catalogue's, and these
/// are strings precisely so nothing here can disagree with it.
/// </para>
/// <para>
/// Nothing enumerates this. The set of currencies is the table's.
/// </para>
/// </remarks>
public static class CurrencyCodes
{
    /// <summary>IslaPay's own unit.</summary>
    public const string EIsla = "EISLA";

    public const string Usdt = "USDT";

    public const string Usdc = "USDC";

    /// <summary>The Cuban peso. A platform liability, never a customer balance.</summary>
    public const string Cup = "CUP";
}

/// <summary>The codes this module refuses with.</summary>
public static class CatalogErrors
{
    /// <summary>
    /// The currency is not listed, or is listed and switched off.
    /// </summary>
    /// <remarks>
    /// One code for both, on purpose, and the same reasoning as
    /// <see cref="ICurrencyCatalog.Require"/>: telling a client which of the
    /// two it is tells it which codes exist, and neither answer changes what
    /// it can do about it.
    /// </remarks>
    public const string CurrencyUnavailable = "currency_unavailable";

    /// <summary>An operator named a currency that is not in the table.</summary>
    /// <remarks>
    /// Distinct from <see cref="CurrencyUnavailable"/> and only ever seen by
    /// an operator: on the admin side "there is no such row" is exactly the
    /// information wanted, since the request is to change a row.
    /// </remarks>
    public const string UnknownCurrency = "unknown_currency";

    /// <summary>An operator named a chain that is not in the table.</summary>
    public const string UnknownNetwork = "unknown_network";
}

/// <summary>
/// A refusal from the catalogue.
/// </summary>
/// <remarks>
/// An <see cref="IApiFailure"/> rather than a plain exception because it
/// reaches the edge: a client may well send an amount in a currency this
/// build does not allow, and that is a request the server understood and
/// declined — 422 — not a fault. Before this it surfaced as a 500, which told
/// the caller to retry something that will never work.
/// </remarks>
public sealed class UnknownCurrencyException : Exception, IApiFailure
{
    public UnknownCurrencyException(string? code, string reason)
        : base($"'{code}' is not a currency this build can use: {reason}") =>
        Code = code ?? string.Empty;

    /// <summary>The currency code that was refused.</summary>
    public string Code { get; }

    string IApiFailure.Code => CatalogErrors.CurrencyUnavailable;

    int IApiFailure.Status => 422;

    IReadOnlyDictionary<string, object>? IApiFailure.Meta =>
        new Dictionary<string, object>(StringComparer.Ordinal) { ["currency"] = Code };
}
