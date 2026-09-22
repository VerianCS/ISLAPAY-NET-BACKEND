using System.Text.Json;
using System.Text.Json.Serialization;
using IslaPay.Platform;

namespace IslaPay.Platform.Serialization;

/// <summary>
/// The one place the wire format is decided.
/// </summary>
/// <remarks>
/// Services and tests both use these options, so a serialisation setting can
/// never be right in one service and wrong in another. Changing anything here
/// changes the contract, which is why it lives in the contracts package rather
/// than in each service's startup.
/// </remarks>
public static class IslaPayJson
{
    /// <summary>
    /// Options that can write anything and cannot read money.
    /// </summary>
    /// <remarks>
    /// Everything the outbox, the bus and the problem writer do is writing,
    /// and a <see cref="Money"/> carries its own scale, so writing needs no
    /// catalogue. Reading one does, and these say so instead of guessing —
    /// see <see cref="Create"/>.
    /// </remarks>
    public static JsonSerializerOptions Options { get; } = Create();

    /// <param name="scales">
    /// Where a currency code's decimal places come from when reading. In the
    /// application this is the catalogue; omitted, money can be written but
    /// not read back.
    /// </param>
    public static JsonSerializerOptions Create(ICurrencyScales? scales = null)
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            // A property the receiver does not know about is normal during a
            // rolling deploy; it is not an error.
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Skip,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        };

        options.Converters.Add(new MoneyJsonConverter(scales));
        options.Converters.Add(new Utc8601Converter());
        // Enums travel as lowercase names, not ordinals. An ordinal is
        // unreadable in a log and silently changes meaning if anyone ever
        // reorders the enum.
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        return options;
    }
}
