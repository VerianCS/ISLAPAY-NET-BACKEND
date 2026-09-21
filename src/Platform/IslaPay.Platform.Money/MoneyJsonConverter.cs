using System.Text.Json;
using System.Text.Json.Serialization;

namespace IslaPay.Platform;

/// <summary>
/// Reads and writes the contract's money shape:
/// <c>{"amount": "100.50", "currency": "EISLA"}</c>.
/// </summary>
/// <remarks>
/// The amount is a JSON string on purpose. A JSON number would be parsed as a
/// double by most clients — including, until it is migrated, ours — and the
/// value would lose exactness at the edge no matter how careful both sides are
/// internally. A string crosses the boundary intact.
/// </remarks>
public sealed class MoneyJsonConverter : JsonConverter<Money>
{
    public override Money Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.StartObject)
        {
            throw new JsonException("Expected an object with 'amount' and 'currency'.");
        }

        string? amount = null;
        string? currency = null;

        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.EndObject) break;
            if (reader.TokenType != JsonTokenType.PropertyName) continue;

            var property = reader.GetString();
            reader.Read();

            switch (property)
            {
                case "amount":
                    // A JSON number here is a client that has not been
                    // migrated; say so rather than silently coercing it.
                    if (reader.TokenType == JsonTokenType.Number)
                    {
                        throw new JsonException(
                            "'amount' must be a string, not a number: a number is read as a " +
                            "float and loses exactness. Send \"100.50\", not 100.50.");
                    }

                    amount = reader.GetString();
                    break;

                case "currency":
                    currency = reader.GetString();
                    break;
            }
        }

        if (amount is null || currency is null)
        {
            throw new JsonException("Both 'amount' and 'currency' are required.");
        }

        return Money.TryParse(amount, currency, out var money)
            ? money
            : throw new JsonException($"'{amount}' {currency} is not a valid amount.");
    }

    public override void Write(Utf8JsonWriter writer, Money value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        writer.WriteString("amount", value.ToString());
        writer.WriteString("currency", value.Currency.Code());
        writer.WriteEndObject();
    }
}
