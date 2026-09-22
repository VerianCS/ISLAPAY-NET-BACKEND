using IslaPay.Catalog.Contracts;
using IslaPay.Platform;
using IslaPay.TestSupport;

namespace IslaPay.Catalog.Tests;

/// <summary>
/// What the catalogue does with the rows, against a real database.
/// </summary>
/// <remarks>
/// These are the behaviours the rest of the system leans on: that a switched
/// off currency cannot be used but can still be read, that a scale cannot
/// change once anything has been stored in it, and that switching a chain off
/// switches off everything on it.
/// </remarks>
[Collection(CatalogDefinition.Name)]
[Trait("Category", "Integration")]
public class CurrencyCatalogTests
{
    private readonly CatalogFixture _fixture;

    public CurrencyCatalogTests(CatalogFixture fixture) => _fixture = fixture;

    [SkippableFact]
    public async Task A_listed_currency_carries_its_scale()
    {
        Skip.IfNot(_fixture.Available, "No Postgres reachable.");
        var catalog = await _fixture.LoadedAsync();

        Assert.Equal(Currency.Of("USDT", 6), catalog.Require(CurrencyCodes.Usdt));
        Assert.Equal(Currency.Of("EISLA", 2), catalog.Require(CurrencyCodes.EIsla));
    }

    /// <summary>
    /// A currency that is off cannot be used and can still be read.
    /// </summary>
    /// <remarks>
    /// Both halves matter and they pull in opposite directions. Refusing it
    /// outright would break every statement containing an old entry; allowing
    /// it would let a withdrawn jurisdiction keep trading. So writing asks
    /// <c>Require</c> and reading asks <c>Describe</c>.
    /// </remarks>
    [SkippableFact]
    public async Task A_switched_off_currency_is_unusable_and_still_legible()
    {
        Skip.IfNot(_fixture.Available, "No Postgres reachable.");
        var catalog = await _fixture.LoadedAsync();

        Assert.Throws<UnknownCurrencyException>(() => catalog.Require("MXN"));
        Assert.False(catalog.TryFind("MXN", out _));

        var described = catalog.Describe("MXN");
        Assert.NotNull(described);
        Assert.False(described.Enabled);
        Assert.True(catalog.TryGetScale("MXN", out var scale));
        Assert.Equal(2, scale);
    }

    /// <summary>
    /// The peso is listed for P2P and is not a wallet anybody opens.
    /// </summary>
    /// <remarks>
    /// The distinction the old enum could not express at all: CUP is a real
    /// currency this system owes people in, through escrow, and never one a
    /// customer holds a balance in.
    /// </remarks>
    [SkippableFact]
    public async Task A_platform_liability_is_enabled_and_not_holdable()
    {
        Skip.IfNot(_fixture.Available, "No Postgres reachable.");
        var catalog = await _fixture.LoadedAsync();

        var cup = catalog.Describe(CurrencyCodes.Cup);
        Assert.NotNull(cup);
        Assert.True(cup.Enabled);
        Assert.False(cup.CustomerHoldable);
        Assert.DoesNotContain(catalog.Holdable, c => c.Code == CurrencyCodes.Cup);
    }

    [SkippableFact]
    public async Task An_asset_on_a_switched_off_chain_is_switched_off()
    {
        Skip.IfNot(_fixture.Available, "No Postgres reachable.");
        var catalog = await _fixture.LoadedAsync();

        var tron = catalog.OnNetwork(CurrencyCodes.Usdt, "tron");
        Assert.NotNull(tron);
        Assert.True(tron.Enabled);
        Assert.Equal("TRC-20", tron.TokenStandard);

        // The pair's own row says enabled for none of these, and the chain
        // says so for all of them. Either is enough.
        Assert.False(catalog.OnNetwork(CurrencyCodes.Usdt, "ethereum")!.Enabled);
        Assert.False(catalog.OnNetwork(CurrencyCodes.Usdc, "solana")!.Enabled);
    }

    [SkippableFact]
    public async Task An_asset_that_is_on_no_chain_has_no_pairs()
    {
        Skip.IfNot(_fixture.Available, "No Postgres reachable.");
        var catalog = await _fixture.LoadedAsync();

        Assert.Empty(catalog.NetworksFor(CurrencyCodes.EIsla));
        Assert.Empty(catalog.NetworksFor(CurrencyCodes.Cup));
        Assert.Null(catalog.OnNetwork(CurrencyCodes.EIsla, "tron"));
    }

    /// <summary>
    /// A scale cannot be changed once the table exists, and the database is
    /// what refuses it.
    /// </summary>
    /// <remarks>
    /// The single most important rule here, and the reason it is safe for
    /// <c>Money</c> to carry its scale and for rows to store it. Changing
    /// USDT from six decimal places to two would not reprice anything — it
    /// would silently restate every stored balance by a factor of ten
    /// thousand. A trigger refuses it, so no migration, script or console
    /// session can do it by accident.
    /// </remarks>
    [SkippableFact]
    public async Task The_database_refuses_to_change_a_scale()
    {
        Skip.IfNot(_fixture.Available, "No Postgres reachable.");

        var refused = await Assert.ThrowsAsync<Npgsql.PostgresException>(() =>
            _fixture.ExecuteAsync(
                "UPDATE catalog.currencies SET scale = 2 WHERE code = 'USDT';"));

        Assert.Contains("scale", refused.MessageText, StringComparison.OrdinalIgnoreCase);
    }

    [SkippableFact]
    public async Task A_refresh_picks_up_a_switch_without_a_restart()
    {
        Skip.IfNot(_fixture.Available, "No Postgres reachable.");
        var catalog = await _fixture.LoadedAsync();

        Assert.Throws<UnknownCurrencyException>(() => catalog.Require("BRL"));

        await _fixture.ExecuteAsync(
            "UPDATE catalog.currencies SET enabled = true WHERE code = 'BRL';");
        await catalog.RefreshAsync();

        // The whole point of the table: a currency was switched on and nothing
        // was deployed, restarted or recompiled.
        Assert.Equal(Currency.Of("BRL", 2), catalog.Require("BRL"));

        await _fixture.ExecuteAsync(
            "UPDATE catalog.currencies SET enabled = false WHERE code = 'BRL';");
        await catalog.RefreshAsync();
    }
}
