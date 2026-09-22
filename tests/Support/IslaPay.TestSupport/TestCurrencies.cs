using IslaPay.Platform;

namespace IslaPay.TestSupport;

/// <summary>
/// The currencies a test names, with the scales the catalogue seeds.
/// </summary>
/// <remarks>
/// <para>
/// Currencies stopped being an <c>enum</c>, and a test still has to be able
/// to write "two E-ISLA" in one short phrase. What it must not do is invent a
/// scale: a suite that quietly decided USDT had two decimal places would pass
/// while the running system accounted it in six, and every amount either of
/// them agreed on would be wrong by ten thousand.
/// </para>
/// <para>
/// So these are stated once, here, and <c>CatalogDriftTests</c> asserts every
/// one of them against <c>catalog.currencies</c> as the migration actually
/// seeds it. The constants are a convenience; the table is the truth, and the
/// test is what stops the two from parting company.
/// </para>
/// <para>
/// It lives in the support project rather than in a module's tests because
/// six suites need it and none of them owns it — and it can, because a
/// <see cref="Currency"/> is a platform type. Nothing here references a
/// module, which is the rule this project is otherwise careful about.
/// </para>
/// </remarks>
public static class TestCurrencies
{
    /// <summary>IslaPay's own unit. Two decimal places, like a dollar.</summary>
    public static readonly Currency EIsla = Currency.Of("EISLA", 2);

    /// <summary>Tether. Six, as on TRON and Ethereum both.</summary>
    public static readonly Currency Usdt = Currency.Of("USDT", 6);

    /// <summary>Circle's dollar. Six.</summary>
    public static readonly Currency Usdc = Currency.Of("USDC", 6);

    /// <summary>The Cuban peso. A platform liability, never a wallet.</summary>
    public static readonly Currency Cup = Currency.Of("CUP", 2);

    /// <summary>The Mexican peso — listed, and switched off.</summary>
    /// <remarks>
    /// Here so a test can ask what happens to a currency the catalogue knows
    /// and will not allow. That case has no representative among the four
    /// above, and it is the one the whole table exists to make possible.
    /// </remarks>
    public static readonly Currency Mxn = Currency.Of("MXN", 2);

    /// <summary>What a customer may hold, in the order the seed lists them.</summary>
    public static readonly IReadOnlyList<Currency> Holdable = [EIsla, Usdt, Usdc];

    /// <summary>Scales for anything that has to read an amount off the wire.</summary>
    public static ICurrencyScales Scales { get; } =
        new StatedScales(EIsla, Usdt, Usdc, Cup, Mxn);
}
