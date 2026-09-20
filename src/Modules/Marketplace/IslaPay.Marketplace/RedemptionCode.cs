using System.Security.Cryptography;
using System.Text;

namespace IslaPay.Marketplace;

/// <summary>
/// The code the buyer shows and the seller scans.
/// </summary>
/// <remarks>
/// <para>
/// Crockford's base32 alphabet: no <c>I</c>, <c>L</c>, <c>O</c> or <c>U</c>.
/// The first three are dropped because they are indistinguishable from
/// <c>1</c>, <c>1</c> and <c>0</c> on a cracked phone screen in daylight, and
/// this code gets read aloud and typed in when the camera will not focus. The
/// fourth is dropped so the generator cannot produce an obscenity.
/// </para>
/// <para>
/// Twelve characters is sixty bits. The code is not a password — it is useless
/// to anyone who is not the authenticated seller of that exact listing — but it
/// is the only thing standing between a seller and collecting without handing
/// anything over, so guessing one must be hopeless rather than merely hard.
/// At sixty bits it is hopeless.
/// </para>
/// <para>
/// Stored as issued rather than hashed, and that is a deliberate trade. The
/// buyer has to be able to reopen the order and show the code again — on a new
/// phone, after reinstalling — which a hash cannot serve. What a hash would
/// protect against is a database dump, and an attacker holding the orders
/// table has the ledger too. What it would cost is the buyer standing in front
/// of the seller with no way to pay.
/// </para>
/// </remarks>
public static class RedemptionCode
{
    private const string Alphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";
    private const int Length = 12;

    /// <summary>A fresh code, formatted the way it is shown: <c>XXXX-XXXX-XXXX</c>.</summary>
    public static string New()
    {
        var chars = new char[Length];
        for (var i = 0; i < Length; i++)
        {
            // One draw per character rather than slicing bytes: 32 does not
            // divide 256 unevenly, but RandomNumberGenerator.GetInt32 is
            // unbiased by construction and nobody has to check the arithmetic.
            chars[i] = Alphabet[RandomNumberGenerator.GetInt32(Alphabet.Length)];
        }

        return Format(new string(chars));
    }

    /// <summary>
    /// What a scanned or typed code means, or null if it cannot mean anything.
    /// </summary>
    /// <remarks>
    /// Forgiving on the way in, because the alternative is a seller staring at
    /// "invalid code" while holding a phone that shows a valid one. Case is
    /// folded, separators of any kind are dropped, and the four characters the
    /// alphabet excludes are folded to the digits they look like — so
    /// <c>islo-…</c> typed by someone reading <c>1510-…</c> still works.
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
                // A code that is too long is not a code with a typo, it is a
                // different string. Stop rather than truncate to a match.
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

    private static string Format(string raw) =>
        $"{raw[..4]}-{raw[4..8]}-{raw[8..]}";
}
