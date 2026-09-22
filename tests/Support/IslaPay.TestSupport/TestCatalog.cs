using IslaPay.Catalog.Contracts;
using IslaPay.Platform;

namespace IslaPay.TestSupport;

/// <summary>
/// The catalogue a module's own tests answer from.
/// </summary>
/// <remarks>
/// <para>
/// Not a stand-in for the real one — a statement of the same rows, made where
/// a module's tests are allowed to see them. The architecture rules stop a
/// module's tests from reaching a sibling's internals, and
/// <c>PostgresCurrencyCatalog</c> is Catalog's internals, so a P2P test cannot
/// construct one. What it can do is say what the table says.
/// </para>
/// <para>
/// Which is only safe if the two cannot drift apart, so they are held together
/// by a test rather than by care: <c>CatalogDriftTests</c> loads the real
/// catalogue over the real migration and asserts, row by row, that this agrees
/// with it. The day somebody changes a scale in the seed and not here, that
/// test fails — which is the whole point, because the alternative is a suite
/// that passes while accounting USDT in two decimal places.
/// </para>
/// <para>
/// End-to-end tests do not use this. They run the host, which reads the
/// tables.
/// </para>
/// </remarks>
public sealed class TestCatalog : ICurrencyCatalog
{
    /// <summary>The seeded currencies, in the seed's own order.</summary>
    public static readonly IReadOnlyList<CurrencyInfo> SeededCurrencies =
    [
        new("EISLA", "Moneda IslaPay", 2, CurrencyKinds.Internal, "E$", true, true, 10),
        new("USDT", "Tether", 6, CurrencyKinds.Stablecoin, "₮", true, true, 20),
        new("USDC", "USD Coin", 6, CurrencyKinds.Stablecoin, "$", true, true, 30),
        new("CUP", "Peso cubano", 2, CurrencyKinds.Fiat, "$", false, true, 40),
    ];

    /// <summary>The chains, and which of them this build watches.</summary>
    public static readonly IReadOnlyList<NetworkInfo> SeededNetworks =
    [
        new("tron", "TRON", 19, "^T[1-9A-HJ-NP-Za-km-z]{33}$", false, true),
        new("ethereum", "Ethereum", 12, "^0x[0-9a-fA-F]{40}$", false, false),
        new("bsc", "BNB Smart Chain", 15, "^0x[0-9a-fA-F]{40}$", false, false),
        new("polygon", "Polygon", 128, "^0x[0-9a-fA-F]{40}$", false, false),
        new("base", "Base", 12, "^0x[0-9a-fA-F]{40}$", false, false),
        new("solana", "Solana", 32, "^[1-9A-HJ-NP-Za-km-z]{32,44}$", false, false),
    ];

    private readonly Dictionary<string, CurrencyInfo> _currencies;
    private readonly List<CurrencyOnNetwork> _pairs;

    public TestCatalog()
    {
        _currencies = SeededCurrencies.ToDictionary(c => c.Code, StringComparer.OrdinalIgnoreCase);

        var networks = SeededNetworks.ToDictionary(n => n.Id, StringComparer.Ordinal);
        _pairs =
        [
            .. new (string Currency, string Network, string Contract, string Standard, bool On)[]
            {
                ("USDT", "tron", "TR7NHqjeKQxGTCi8q8ZY4pL8otSzgjLj6t", "TRC-20", true),
                ("USDT", "ethereum", "0xdAC17F958D2ee523a2206206994597C13D831ec7", "ERC-20", false),
                ("USDT", "bsc", "0x55d398326f99059fF775485246999027B3197955", "BEP-20", false),
                ("USDT", "polygon", "0xc2132D05D31c914a87C6611C10748AEb04B58e8F", "ERC-20", false),
                ("USDC", "ethereum", "0xA0b86991c6218b36c1d19D4a2e9Eb0cE3606eB48", "ERC-20", false),
                ("USDC", "polygon", "0x3c499c542cEF5E3811e1192ce70d8cC03d5c3359", "ERC-20", false),
                ("USDC", "base", "0x833589fCD6eDb6E08f4c7C32D4f71b54bdA02913", "ERC-20", false),
                ("USDC", "solana", "EPjFWdd5AufqSSqeM2qN1xzybapC8G4wEGGkZwyTDt1v", "SPL", false),
            }.Select(p =>
            {
                var network = networks[p.Network];
                return new CurrencyOnNetwork(
                    p.Currency,
                    network.Id,
                    network.Name,
                    p.Contract,
                    p.Standard,
                    network.Confirmations,
                    network.AddressPattern,
                    network.MemoRequired,
                    MinimumWithdrawalMinor: null,
                    // A pair is usable only if its chain is: switching a chain
                    // off has to switch off everything on it, which is the one
                    // piece of logic the table does not hold by itself.
                    Enabled: p.On && network.Enabled);
            }),
        ];
    }

    public IReadOnlyList<CurrencyInfo> Currencies => SeededCurrencies;

    public IReadOnlyList<CurrencyInfo> Holdable =>
        [.. SeededCurrencies.Where(c => c is { CustomerHoldable: true, Enabled: true })];

    public IReadOnlyList<NetworkInfo> Networks => SeededNetworks;

    public Currency Require(string code)
    {
        var info = Describe(code);
        if (info is null) throw new UnknownCurrencyException(code, "it is not listed.");
        if (!info.Enabled) throw new UnknownCurrencyException(code, "it is switched off.");
        return info.Currency;
    }

    public bool TryFind(string? code, out Currency currency)
    {
        var info = Describe(code);
        currency = info is { Enabled: true } ? info.Currency : default;
        return currency.IsDefined;
    }

    public CurrencyInfo? Describe(string? code) =>
        code is not null && _currencies.TryGetValue(code.Trim(), out var info) ? info : null;

    public IReadOnlyList<CurrencyOnNetwork> NetworksFor(string? currencyCode) =>
        currencyCode is null
            ? []
            : [.. _pairs.Where(p => string.Equals(
                p.CurrencyCode, currencyCode.Trim(), StringComparison.OrdinalIgnoreCase))];

    public CurrencyOnNetwork? OnNetwork(string? currencyCode, string? networkId) =>
        currencyCode is null || networkId is null
            ? null
            : _pairs.FirstOrDefault(p =>
                string.Equals(p.CurrencyCode, currencyCode.Trim(), StringComparison.OrdinalIgnoreCase)
                && string.Equals(p.NetworkId, networkId.Trim(), StringComparison.OrdinalIgnoreCase));

    public bool TryGetScale(string? code, out int scale)
    {
        // Answers for a disabled currency, like the real one: reading an old
        // amount has to keep working after a currency is withdrawn.
        var info = Describe(code);
        scale = info?.Scale ?? 0;
        return info is not null;
    }

    public Task RefreshAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
}
