using IslaPay.Platform;

namespace IslaPay.Catalog.Contracts;

/// <summary>What a currency is, beyond what arithmetic needs.</summary>
/// <param name="Kind">
/// <c>fiat</c>, <c>stablecoin</c> or <c>internal</c>. Not decoration: it is
/// what decides whether an amount can be sent to a chain, whether it needs a
/// payment rail, and which rules a jurisdiction applies to it.
/// </param>
/// <param name="CustomerHoldable">
/// Whether a customer may have a balance in it. False for a currency the
/// platform only owes through escrow — the Cuban peso leg of a P2P trade is a
/// liability of IslaPay, not a wallet anybody opens.
/// </param>
/// <param name="Enabled">
/// Whether it may be used at all right now. A currency is listed long before
/// it is switched on, because listing it is a schema change and switching it
/// on is a business decision that should not need a deploy.
/// </param>
public sealed record CurrencyInfo(
    string Code,
    string Name,
    int Scale,
    string Kind,
    string Symbol,
    bool CustomerHoldable,
    bool Enabled,
    int SortOrder)
{
    /// <summary>The two facts an amount needs.</summary>
    public Currency Currency => Currency.Of(Code, Scale);

    public bool IsOnChain => string.Equals(Kind, CurrencyKinds.Stablecoin, StringComparison.Ordinal);
}

/// <summary>The closed set of currency kinds.</summary>
public static class CurrencyKinds
{
    /// <summary>Money a state issues. Moves through banks and payment rails.</summary>
    public const string Fiat = "fiat";

    /// <summary>A token on one or more chains. Carries a network.</summary>
    public const string Stablecoin = "stablecoin";

    /// <summary>IslaPay's own unit. On no chain and through no bank.</summary>
    public const string Internal = "internal";
}

/// <summary>
/// A chain.
/// </summary>
/// <param name="Confirmations">
/// Blocks after which this build treats a transfer as irreversible. A risk
/// decision, not a protocol constant, and a row rather than a constant so it
/// can be raised the day a chain misbehaves.
/// </param>
/// <param name="AddressPattern">
/// Checked before an address is ever shown. A deposit address is the one
/// string in this system where a typo is unrecoverable.
/// </param>
public sealed record NetworkInfo(
    string Id,
    string Name,
    int Confirmations,
    string AddressPattern,
    bool MemoRequired,
    bool Enabled);

/// <summary>
/// One asset on one chain. The thing that actually exists.
/// </summary>
/// <remarks>
/// Its own table because USDT is not a TRON token or an Ethereum token — it is
/// both, and four more besides, and they are different assets that happen to
/// share a name and a price. Sending Ethereum USDT to a TRON address loses the
/// money. Any model where the currency implies the chain gets that wrong.
/// </remarks>
/// <param name="Contract">
/// The token's address on that chain, empty for a chain's own coin. What makes
/// "USDT on TRON" a specific thing rather than a phrase.
/// </param>
public sealed record CurrencyOnNetwork(
    string CurrencyCode,
    string NetworkId,
    string NetworkName,
    string Contract,
    string TokenStandard,
    int Confirmations,
    string AddressPattern,
    bool MemoRequired,
    long? MinimumWithdrawalMinor,
    bool Enabled);
