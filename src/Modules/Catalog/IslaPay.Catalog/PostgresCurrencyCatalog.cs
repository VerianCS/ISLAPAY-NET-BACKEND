using IslaPay.Catalog.Contracts;
using IslaPay.Platform;
using IslaPay.Platform.Data;
using Npgsql;

namespace IslaPay.Catalog;

/// <summary>
/// The catalogue, read from <c>catalog.*</c> and held as an immutable
/// snapshot.
/// </summary>
/// <remarks>
/// <para>
/// Every currency lookup in the system comes through here, including ones
/// inside arithmetic, so it cannot be a query. It is loaded once and replaced
/// whole: readers take the current snapshot by reference and are never
/// blocked, never see a half-applied change, and never pay for a round trip.
/// </para>
/// <para>
/// Raw Npgsql rather than EF, matching the ledger and the outbox. Three
/// <c>SELECT</c>s run once at start-up do not need a change tracker, and this
/// type sits underneath everything that moves money — the fewer things it
/// depends on, the better.
/// </para>
/// </remarks>
public sealed class PostgresCurrencyCatalog : ICurrencyCatalog, IDisposable
{
    private readonly IDatabase _database;
    private readonly SemaphoreSlim _loading = new(1, 1);
    private Snapshot? _snapshot;

    public PostgresCurrencyCatalog(IDatabase database)
    {
        ArgumentNullException.ThrowIfNull(database);
        _database = database;
    }

    private Snapshot Current =>
        Volatile.Read(ref _snapshot)
        ?? throw new InvalidOperationException(
            "The currency catalogue has not been loaded. The host warms it during "
            + "start-up, before any endpoint is mapped; a test that uses it directly "
            + "has to await RefreshAsync first.");

    public Currency Require(string code)
    {
        var info = Describe(code)
            ?? throw new UnknownCurrencyException(code, "it is not in catalog.currencies.");

        if (!info.Enabled)
        {
            // The same answer as "unknown", on purpose. A currency that has
            // been switched off is one no new amount may be denominated in,
            // and letting one through because it used to work is how a
            // withdrawn jurisdiction keeps trading.
            throw new UnknownCurrencyException(code, "it is switched off.");
        }

        return info.Currency;
    }

    public bool TryFind(string? code, out Currency currency)
    {
        var info = Describe(code);
        if (info is null || !info.Enabled)
        {
            currency = default;
            return false;
        }

        currency = info.Currency;
        return true;
    }

    public CurrencyInfo? Describe(string? code) =>
        code is not null && Current.ByCode.TryGetValue(code.Trim(), out var info) ? info : null;

    /// <remarks>
    /// Answers for a disabled currency too. Scale is needed to <i>read</i> an
    /// amount, and reading an old posting denominated in a currency since
    /// withdrawn has to keep working — history does not stop being true.
    /// </remarks>
    public bool TryGetScale(string? code, out int scale)
    {
        var info = Describe(code);
        scale = info?.Scale ?? 0;
        return info is not null;
    }

    public IReadOnlyList<CurrencyInfo> Currencies => Current.All;

    public IReadOnlyList<CurrencyInfo> Holdable => Current.Holdable;

    public IReadOnlyList<NetworkInfo> Networks => Current.Networks;

    public IReadOnlyList<CurrencyOnNetwork> NetworksFor(string? currencyCode) =>
        currencyCode is not null && Current.ByCurrency.TryGetValue(currencyCode.Trim(), out var list)
            ? list
            : [];

    public CurrencyOnNetwork? OnNetwork(string? currencyCode, string? networkId) =>
        NetworksFor(currencyCode)
            .FirstOrDefault(n => string.Equals(n.NetworkId, networkId?.Trim(), StringComparison.Ordinal));

    /// <remarks>
    /// Serialised, so a burst of admin writes reads the tables once rather
    /// than once each. The snapshot is swapped only when the read completes,
    /// so a failure leaves the previous one serving.
    /// </remarks>
    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        await _loading.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var loaded = await LoadAsync(cancellationToken).ConfigureAwait(false);
            Volatile.Write(ref _snapshot, loaded);
        }
        finally
        {
            _loading.Release();
        }
    }

    private async Task<Snapshot> LoadAsync(CancellationToken cancellationToken)
    {
        await using var connection = await _database.OpenAsync(cancellationToken)
            .ConfigureAwait(false);

        var currencies = new List<CurrencyInfo>();
        await using (var command = new NpgsqlCommand(
            """
            SELECT code, name, scale, kind, symbol, customer_holdable, enabled, sort_order
              FROM catalog.currencies
             ORDER BY sort_order, code
            """, connection))
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false))
        {
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                currencies.Add(new CurrencyInfo(
                    Code: reader.GetString(0),
                    Name: reader.GetString(1),
                    Scale: reader.GetInt32(2),
                    Kind: reader.GetString(3),
                    Symbol: reader.GetString(4),
                    CustomerHoldable: reader.GetBoolean(5),
                    Enabled: reader.GetBoolean(6),
                    SortOrder: reader.GetInt32(7)));
            }
        }

        var networks = new List<NetworkInfo>();
        await using (var command = new NpgsqlCommand(
            """
            SELECT id, name, confirmations, address_pattern, memo_required, enabled
              FROM catalog.networks
             ORDER BY id
            """, connection))
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false))
        {
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                networks.Add(new NetworkInfo(
                    Id: reader.GetString(0),
                    Name: reader.GetString(1),
                    Confirmations: reader.GetInt32(2),
                    AddressPattern: reader.GetString(3),
                    MemoRequired: reader.GetBoolean(4),
                    Enabled: reader.GetBoolean(5)));
            }
        }

        var pairs = new List<CurrencyOnNetwork>();
        await using (var command = new NpgsqlCommand(
            """
            SELECT cn.currency_code, cn.network_id, n.name, cn.contract, cn.token_standard,
                   n.confirmations, n.address_pattern, n.memo_required,
                   cn.minimum_withdrawal_minor,
                   -- A pair is usable only if the chain is too. Switching a
                   -- chain off has to switch off every asset on it, or a
                   -- disabled chain keeps taking deposits through the back.
                   cn.enabled AND n.enabled
              FROM catalog.currency_networks cn
              JOIN catalog.networks n ON n.id = cn.network_id
             ORDER BY cn.currency_code, cn.network_id
            """, connection))
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false))
        {
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                pairs.Add(new CurrencyOnNetwork(
                    CurrencyCode: reader.GetString(0),
                    NetworkId: reader.GetString(1),
                    NetworkName: reader.GetString(2),
                    Contract: reader.GetString(3),
                    TokenStandard: reader.GetString(4),
                    Confirmations: reader.GetInt32(5),
                    AddressPattern: reader.GetString(6),
                    MemoRequired: reader.GetBoolean(7),
                    MinimumWithdrawalMinor: reader.IsDBNull(8) ? null : reader.GetInt64(8),
                    Enabled: reader.GetBoolean(9)));
            }
        }

        return Snapshot.From(currencies, networks, pairs);
    }

    public void Dispose() => _loading.Dispose();

    /// <summary>One consistent reading of the three tables.</summary>
    private sealed record Snapshot(
        IReadOnlyDictionary<string, CurrencyInfo> ByCode,
        IReadOnlyList<CurrencyInfo> All,
        IReadOnlyList<CurrencyInfo> Holdable,
        IReadOnlyList<NetworkInfo> Networks,
        IReadOnlyDictionary<string, IReadOnlyList<CurrencyOnNetwork>> ByCurrency)
    {
        public static Snapshot From(
            List<CurrencyInfo> currencies,
            List<NetworkInfo> networks,
            List<CurrencyOnNetwork> pairs)
        {
            var byCurrency = pairs
                .GroupBy(p => p.CurrencyCode, StringComparer.Ordinal)
                .ToDictionary(
                    g => g.Key,
                    g => (IReadOnlyList<CurrencyOnNetwork>)[.. g],
                    StringComparer.OrdinalIgnoreCase);

            return new Snapshot(
                ByCode: currencies.ToDictionary(c => c.Code, StringComparer.OrdinalIgnoreCase),
                All: currencies,
                Holdable: [.. currencies.Where(c => c is { Enabled: true, CustomerHoldable: true })],
                Networks: networks,
                ByCurrency: byCurrency);
        }
    }
}
