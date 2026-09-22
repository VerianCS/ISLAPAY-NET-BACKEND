using System.Text.RegularExpressions;

namespace IslaPay.Platform;

/// <summary>
/// A currency, as an amount needs to know it: a code and the number of decimal
/// places it is accounted in.
/// </summary>
/// <remarks>
/// <para>
/// This was an <c>enum</c> with four members, and that was the single thing
/// stopping the system from being multi-currency. An enum is a closed set
/// decided at compile time: adding the Mexican peso meant a deploy, adding
/// forty of them meant a deploy and a migration of every account name, and
/// switching one off for a jurisdiction was not expressible at all. A
/// catalogue in the database is none of those things.
/// </para>
/// <para>
/// What is left here is only what arithmetic and rendering need. Deliberately
/// <b>not</b> here: whether a currency is on a chain, whether a customer may
/// hold it, whether it is switched on. Those are policy, they change without a
/// deploy, and a type that answered them from its own fields would be a second
/// catalogue quietly disagreeing with the first. Ask <c>ICurrencyCatalog</c>.
/// </para>
/// <para>
/// The scale travels with the code on purpose. A <see cref="Money"/> that has
/// to look its scale up is a <see cref="Money"/> that can be rendered
/// differently in two places, and the two places are usually a receipt and a
/// ledger.
/// </para>
/// </remarks>
public readonly record struct Currency
{
    /// <summary>
    /// The most decimal places any currency here may have.
    /// </summary>
    /// <remarks>
    /// Eighteen is what an ERC-20 may declare, and it is also where
    /// <c>long</c> minor units stop being able to hold a meaningful amount —
    /// one whole unit of an 18-decimal asset is already 10^18, most of the
    /// range. Anything that needs more than this needs a wider integer, which
    /// is a decision rather than a configuration value.
    /// </remarks>
    public const int MaximumScale = 18;

    private static readonly Regex CodeShape = new(
        "^[A-Z0-9]{2,12}$", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));

    private Currency(string code, int scale)
    {
        Code = code;
        Scale = scale;
    }

    /// <summary>The wire code: <c>EISLA</c>, <c>USDT</c>, <c>MXN</c>.</summary>
    public string Code { get; }

    /// <summary>
    /// Decimal places this currency is accounted in.
    /// </summary>
    /// <remarks>
    /// The whole reason <see cref="Money"/> can be an integer: a balance is
    /// always a whole number of these units, never a fraction of one.
    /// </remarks>
    public int Scale { get; }

    /// <summary>
    /// False for <c>default(Currency)</c>, which is not a currency.
    /// </summary>
    /// <remarks>
    /// A struct always has a zero value and this one's is meaningless — code
    /// null, scale nought. It is checked rather than tolerated: a
    /// <c>default(Money)</c> that quietly behaved like zero of something would
    /// balance a posting it had no business balancing.
    /// </remarks>
    public bool IsDefined => Code is not null;

    /// <summary>
    /// Builds one from a code and a scale.
    /// </summary>
    /// <remarks>
    /// The only way to make a <see cref="Currency"/>, and it takes data rather
    /// than naming anything. In the application these two facts come from
    /// <c>catalog.currencies</c>; a test may state them directly, which is the
    /// same thing said sooner.
    /// </remarks>
    public static Currency Of(string code, int scale)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(code);

        var normalised = code.Trim().ToUpperInvariant();
        if (!CodeShape.IsMatch(normalised))
        {
            throw new ArgumentException(
                $"'{code}' is not a currency code: two to twelve characters, "
                + "letters and digits only.",
                nameof(code));
        }

        ArgumentOutOfRangeException.ThrowIfNegative(scale);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(scale, MaximumScale);

        return new Currency(normalised, scale);
    }

    /// <summary>The code, so interpolation and logs read naturally.</summary>
    public override string ToString() => Code ?? "(none)";
}

/// <summary>
/// Somewhere that knows how many decimal places a code is accounted in.
/// </summary>
/// <remarks>
/// <para>
/// The one thing that cannot be answered from the wire. A
/// <see cref="Money"/> already carries its scale, so writing one needs nothing;
/// <i>reading</i> one gets a code and a decimal string and has to decide
/// whether <c>"1.5"</c> in USDT means fifteen hundred thousand minor units or
/// a malformed amount.
/// </para>
/// <para>
/// Narrow on purpose. <c>ICurrencyCatalog</c> implements it, and nothing that
/// only needs to parse an amount has to take the whole catalogue to do it.
/// </para>
/// </remarks>
public interface ICurrencyScales
{
    bool TryGetScale(string? code, out int scale);
}

/// <summary>
/// Scales stated outright, for code that has no catalogue to ask.
/// </summary>
/// <remarks>
/// Its uses are bootstrapping and tests. It is not a catalogue: it knows
/// nothing about whether a currency is enabled, holdable or on a chain, and it
/// cannot answer a question it was not built with.
/// </remarks>
public sealed class StatedScales : ICurrencyScales
{
    private readonly Dictionary<string, int> _scales;

    public StatedScales(IReadOnlyDictionary<string, int> scales)
    {
        ArgumentNullException.ThrowIfNull(scales);
        _scales = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var (code, scale) in scales) _scales[code.Trim()] = scale;
    }

    public StatedScales(params Currency[] currencies)
        : this((currencies ?? []).ToDictionary(c => c.Code, c => c.Scale, StringComparer.Ordinal))
    {
    }

    /// <summary>
    /// Refuses every code.
    /// </summary>
    /// <remarks>
    /// The default for serializer options built without a catalogue. Writing
    /// money still works — a <see cref="Money"/> knows its own scale — and
    /// reading one fails with a message that names the fix, rather than
    /// guessing a scale and being wrong by a factor of ten thousand.
    /// </remarks>
    public static ICurrencyScales None { get; } = new StatedScales([]);

    public bool TryGetScale(string? code, out int scale)
    {
        scale = 0;
        return code is not null && _scales.TryGetValue(code.Trim(), out scale);
    }
}
