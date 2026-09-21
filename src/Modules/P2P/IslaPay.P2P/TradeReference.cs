using System.Security.Cryptography;
using System.Text;

namespace IslaPay.P2P;

/// <summary>
/// The short code both sides of a trade quote at each other.
/// </summary>
/// <remarks>
/// <para>
/// Not a secret, and that is the difference between this and the
/// marketplace's redemption code. That one authorises a payment and is sixty
/// bits because guessing it has to be hopeless. This one only names a trade:
/// an operator matches it against a bank transfer, a buyer writes it in a
/// payment note, and support reads it back over the phone. Knowing somebody
/// else's changes nothing, because every operation that acts on a trade is
/// authenticated as the user or the operator.
/// </para>
/// <para>
/// So it is eight characters rather than twelve — forty bits, short enough to
/// fit in a Transfermóvil note and to dictate without mistakes. Collisions are
/// caught by a unique index rather than made impossible by size.
/// </para>
/// <para>
/// Crockford's alphabet for the same reason as the marketplace's: no
/// <c>I</c>, <c>L</c>, <c>O</c> or <c>U</c>, because this gets read aloud.
/// </para>
/// </remarks>
public static class TradeReference
{
    private const string Alphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";
    private const int Length = 8;

    /// <summary>A fresh reference, formatted the way it is shown: <c>XXXX-XXXX</c>.</summary>
    public static string New()
    {
        var chars = new char[Length];
        for (var i = 0; i < Length; i++)
        {
            chars[i] = Alphabet[RandomNumberGenerator.GetInt32(Alphabet.Length)];
        }

        return Format(new string(chars));
    }

    /// <summary>
    /// What a typed or pasted reference means, or null if it cannot mean
    /// anything.
    /// </summary>
    /// <remarks>
    /// Forgiving in the same way and for the same reason: an operator reading
    /// a customer's screenshot should not lose ten minutes to a letter that
    /// looks like a digit.
    /// </remarks>
    public static string? Normalise(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;

        var builder = new StringBuilder(Length);
        foreach (var character in raw)
        {
            var upper = char.ToUpperInvariant(character);
            var folded = upper switch
            {
                'I' or 'L' => '1',
                'O' => '0',
                'U' => 'V',
                _ => upper,
            };

            if (Alphabet.Contains(folded, StringComparison.Ordinal))
            {
                if (builder.Length == Length) return null;
                builder.Append(folded);
            }
            else if (folded is not ('-' or ' ' or '_'))
            {
                return null;
            }
        }

        return builder.Length == Length ? Format(builder.ToString()) : null;
    }

    private static string Format(string raw) => $"{raw[..4]}-{raw[4..]}";
}
