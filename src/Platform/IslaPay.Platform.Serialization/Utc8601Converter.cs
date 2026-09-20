using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace IslaPay.Platform.Serialization;

/// <summary>
/// Writes timestamps as RFC 3339 UTC ending in <c>Z</c>.
/// </summary>
/// <remarks>
/// The default for <see cref="DateTimeOffset"/> renders a zero offset as
/// <c>+00:00</c>, which is the same instant but not the form §11.1 of the
/// architecture specifies. Pinning it matters because timestamps end up in
/// logs, receipts and reconciliation files that are compared as text, and
/// two spellings of the same instant do not compare equal.
/// <para>
/// Reading is permissive — any offset is accepted and normalised to UTC —
/// because being strict about input buys nothing here.
/// </para>
/// </remarks>
public sealed class Utc8601Converter : JsonConverter<DateTimeOffset>
{
    private const string Format = "yyyy-MM-dd'T'HH:mm:ss.fff'Z'";

    public override DateTimeOffset Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        reader.GetDateTimeOffset().ToUniversalTime();

    public override void Write(Utf8JsonWriter writer, DateTimeOffset value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value.ToUniversalTime().ToString(Format, CultureInfo.InvariantCulture));
}
