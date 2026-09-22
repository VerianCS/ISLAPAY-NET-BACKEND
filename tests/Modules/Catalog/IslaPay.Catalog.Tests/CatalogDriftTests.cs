using IslaPay.Catalog.Contracts;
using IslaPay.TestSupport;

namespace IslaPay.Catalog.Tests;

/// <summary>
/// Holds the stated catalogue every other suite tests against to the one the
/// migration actually seeds.
/// </summary>
/// <remarks>
/// <para>
/// The reason <c>TestCatalog</c> is safe to exist. A module's tests cannot
/// construct <c>PostgresCurrencyCatalog</c> — it is another module's
/// internals — so they answer from a stated copy of the same rows. A stated
/// copy is only as good as the guarantee that it still matches, and "we will
/// remember to update both" is not a guarantee.
/// </para>
/// <para>
/// So this is the guarantee. If somebody changes a scale, a name, a switch or
/// a contract address in <c>001_catalog.sql</c> and not in
/// <c>TestCatalog</c>, this fails and names the row. The alternative is six
/// suites passing while accounting USDT in the wrong number of decimal
/// places, which is a bug you find in a balance rather than in a test.
/// </para>
/// </remarks>
[Collection(CatalogDefinition.Name)]
[Trait("Category", "Integration")]
public class CatalogDriftTests
{
    private readonly CatalogFixture _fixture;

    public CatalogDriftTests(CatalogFixture fixture) => _fixture = fixture;

    [SkippableFact]
    public async Task Every_stated_currency_is_the_seeded_one()
    {
        Skip.IfNot(_fixture.Available, "No Postgres reachable.");
        var catalog = await _fixture.LoadedAsync();

        foreach (var stated in TestCatalog.SeededCurrencies)
        {
            var seeded = catalog.Describe(stated.Code);
            Assert.True(seeded is not null, $"{stated.Code} is stated but not seeded.");
            Assert.Equal(stated, seeded);
        }
    }

    [SkippableFact]
    public async Task Every_stated_network_is_the_seeded_one()
    {
        Skip.IfNot(_fixture.Available, "No Postgres reachable.");
        var catalog = await _fixture.LoadedAsync();

        foreach (var stated in TestCatalog.SeededNetworks)
        {
            var seeded = Assert.Single(catalog.Networks, n => n.Id == stated.Id);
            Assert.Equal(stated, seeded);
        }

        Assert.Equal(TestCatalog.SeededNetworks.Count, catalog.Networks.Count);
    }

    [SkippableFact]
    public async Task Every_stated_pair_is_the_seeded_one()
    {
        Skip.IfNot(_fixture.Available, "No Postgres reachable.");
        var catalog = await _fixture.LoadedAsync();
        var stated = new TestCatalog();

        foreach (var currency in TestCatalog.SeededCurrencies)
        {
            Assert.Equal(
                stated.NetworksFor(currency.Code).OrderBy(p => p.NetworkId, StringComparer.Ordinal),
                catalog.NetworksFor(currency.Code).OrderBy(p => p.NetworkId, StringComparer.Ordinal));
        }
    }

    /// <summary>
    /// The scales the shared test constants carry are the table's.
    /// </summary>
    /// <remarks>
    /// <c>TestCurrencies</c> is what five suites write amounts with. A scale
    /// wrong here is every one of those suites asserting a figure the running
    /// system would never produce.
    /// </remarks>
    [SkippableFact]
    public async Task The_shared_test_currencies_carry_the_seeded_scales()
    {
        Skip.IfNot(_fixture.Available, "No Postgres reachable.");
        var catalog = await _fixture.LoadedAsync();

        foreach (var currency in new[]
        {
            TestCurrencies.EIsla, TestCurrencies.Usdt, TestCurrencies.Usdc,
            TestCurrencies.Cup, TestCurrencies.Mxn,
        })
        {
            var seeded = catalog.Describe(currency.Code);
            Assert.True(seeded is not null, $"{currency.Code} is not seeded.");
            Assert.Equal(currency.Scale, seeded.Scale);
        }
    }

    [SkippableFact]
    public async Task What_a_customer_may_hold_is_what_the_test_constants_say()
    {
        Skip.IfNot(_fixture.Available, "No Postgres reachable.");
        var catalog = await _fixture.LoadedAsync();

        Assert.Equal(
            TestCurrencies.Holdable,
            catalog.Holdable.Select(c => c.Currency).ToList());
    }

    /// <summary>
    /// <c>USD</c> is the US dollar and not IslaPay's own unit.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The internal unit used to be called USD and is not a US dollar —
    /// IslaPay does not issue those. While currencies were an enum the two
    /// could not coexist, and the rename was the whole fix. Now they are two
    /// rows, which is the right answer and also a new way to get it wrong:
    /// somebody could switch USD on for customers and quietly reintroduce the
    /// confusion the rename cost a migration to remove.
    /// </para>
    /// <para>
    /// So the rule is stated rather than assumed: USD is listed, because the
    /// dollar is a real currency this market quotes against, and it is off,
    /// not holdable, and not E-ISLA.
    /// </para>
    /// </remarks>
    [SkippableTheory]
    [InlineData("USD")]
    [InlineData("usd")]
    public async Task The_dollar_is_listed_and_is_not_the_internal_unit(string code)
    {
        Skip.IfNot(_fixture.Available, "No Postgres reachable.");
        var catalog = await _fixture.LoadedAsync();

        var dollar = catalog.Describe(code);
        Assert.NotNull(dollar);
        Assert.Equal(CurrencyKinds.Fiat, dollar.Kind);
        Assert.False(dollar.Enabled);
        Assert.False(dollar.CustomerHoldable);

        // Not usable, and not a way to spell E-ISLA.
        Assert.Throws<UnknownCurrencyException>(() => catalog.Require(code));
        Assert.NotEqual(catalog.Require(CurrencyCodes.EIsla), dollar.Currency);
        Assert.DoesNotContain(catalog.Holdable, c => c.Code == "USD");
    }
}
