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
    public static JsonSerializerOptions Options { get; } = Create();

    public static JsonSerializerOptions Create()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            // A property the receiver does not know about is normal during a
            // rolling deploy; it is not an error.
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Skip,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        };

        options.Converters.Add(new MoneyJsonConverter());
        options.Converters.Add(new Utc8601Converter());
        // Enums travel as lowercase names, not ordinals. An ordinal is
        // unreadable in a log and silently changes meaning if anyone ever
        // reorders the enum.
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        return options;
    }
}
