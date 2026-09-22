using IslaPay.Catalog.Contracts;
using IslaPay.Platform.Data;
using Npgsql;

namespace IslaPay.Catalog;

/// <summary>
/// The switches: turning a currency, a chain or an asset-on-a-chain on and
/// off.
/// </summary>
/// <remarks>
/// <para>
/// This is what the tables were for. Enabling the Mexican peso used to mean
/// editing an enum, rebuilding, and migrating the account names in the ledger;
/// it is now one row and a snapshot reload.
/// </para>
/// <para>
/// Every write refreshes the catalogue before returning, so the caller's next
/// request sees its own change. That matters more than it sounds: an operator
/// who switches a currency on, reloads the admin page and sees it still off
/// will switch it on again, and the second press is the one that goes into an
/// audit log looking like indecision.
/// </para>
/// </remarks>
public sealed class CatalogAdminService
{
    private readonly IDatabase _database;
    private readonly ICurrencyCatalog _catalog;

    public CatalogAdminService(IDatabase database, ICurrencyCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(catalog);
        _database = database;
        _catalog = catalog;
    }

    public Task SetCurrencyEnabledAsync(
        string code, bool enabled, CancellationToken cancellationToken = default) =>
        SwitchAsync(
            "UPDATE catalog.currencies SET enabled = @v, updated_at = now() WHERE code = @k",
            code.Trim().ToUpperInvariant(),
            enabled,
            CatalogErrors.UnknownCurrency,
            $"'{code}' is not in catalog.currencies.",
            cancellationToken);

    public Task SetNetworkEnabledAsync(
        string id, bool enabled, CancellationToken cancellationToken = default) =>
        SwitchAsync(
            "UPDATE catalog.networks SET enabled = @v, updated_at = now() WHERE id = @k",
            id.Trim(),
            enabled,
            CatalogErrors.UnknownNetwork,
            $"'{id}' is not in catalog.networks.",
            cancellationToken);

    /// <remarks>
    /// Switching the pair on does not switch the chain on. A pair is usable
    /// only when both are, which is read in the catalogue's query — so
    /// enabling USDT-on-Ethereum while Ethereum is off is allowed, recorded,
    /// and has no effect until somebody enables the chain. That is the right
    /// order for a rollout, and the wrong order to discover by surprise.
    /// </remarks>
    public async Task SetPairEnabledAsync(
        string code, string networkId, bool enabled,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await _database.OpenAsync(cancellationToken)
            .ConfigureAwait(false);

        await using var command = new NpgsqlCommand(
            """
            UPDATE catalog.currency_networks
               SET enabled = @v, updated_at = now()
             WHERE currency_code = @c AND network_id = @n
            """, connection);
        command.Parameters.AddWithValue("v", enabled);
        command.Parameters.AddWithValue("c", code.Trim().ToUpperInvariant());
        command.Parameters.AddWithValue("n", networkId.Trim());

        var touched = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        if (touched == 0)
        {
            throw new CatalogException(
                CatalogErrors.UnknownCurrency,
                404,
                $"'{code}' is not listed on '{networkId}'.");
        }

        await _catalog.RefreshAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task SwitchAsync(
        string sql, string key, bool enabled, string errorCode, string missing,
        CancellationToken cancellationToken)
    {
        await using var connection = await _database.OpenAsync(cancellationToken)
            .ConfigureAwait(false);

        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("v", enabled);
        command.Parameters.AddWithValue("k", key);

        var touched = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        if (touched == 0) throw new CatalogException(errorCode, 404, missing);

        await _catalog.RefreshAsync(cancellationToken).ConfigureAwait(false);
    }
}
