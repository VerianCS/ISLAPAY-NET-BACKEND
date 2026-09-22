using IslaPay.Platform;

namespace IslaPay.Custody.Contracts;

/// <summary>
/// A chain IslaPay watches, and what it takes for a transfer on it to be
/// final.
/// </summary>
/// <remarks>
/// <para>
/// <paramref name="Confirmations"/> is the number of blocks after which this
/// build treats a transfer as irreversible. It is a risk decision, not a
/// protocol constant: the chain does not become certain at a particular
/// depth, it becomes expensive enough to rewrite that we stop worrying. Which
/// is why it sits in a contract a reviewer can read rather than in a
/// magic number somewhere in a scanner.
/// </para>
/// <para>
/// <paramref name="AddressPattern"/> is checked before an address is ever
/// shown to anybody. A deposit address is the one string in this system where
/// a typo is unrecoverable — money sent to a wrong-but-valid address is gone,
/// and nothing IslaPay can do afterwards brings it back.
/// </para>
/// </remarks>
public sealed record CustodyNetwork(
    string Id,
    string Name,
    Currency Currency,
    int Confirmations,
    string AddressPattern);

/// <summary>The networks this build knows.</summary>
/// <remarks>
/// One, on purpose. A second network is not a configuration change: it is a
/// scanner, a fee model, a set of failure modes and an address format, and
/// shipping one properly is worth more than shipping three that are each
/// nearly right.
/// </remarks>
public static class CustodyNetworks
{
    /// <summary>
    /// TRON, for USDT (TRC-20).
    /// </summary>
    /// <remarks>
    /// Nineteen confirmations because that is where TRON's own consensus
    /// considers a block irreversible — two thirds of 27 super
    /// representatives having built on it. Below that a reorganisation is a
    /// normal event rather than an attack.
    /// </remarks>
    public const string Tron = "tron";

    public static readonly CustodyNetwork TronUsdt = new(
        Id: Tron,
        Name: "TRON (TRC-20)",
        Currency: Currency.Usdt,
        Confirmations: 19,
        // Base58Check, mainnet: a leading 'T' and 33 more characters from the
        // Bitcoin alphabet — no 0, O, I or l, which is the point of it.
        AddressPattern: "^T[1-9A-HJ-NP-Za-km-z]{33}$");

    public static IReadOnlyList<CustodyNetwork> All { get; } = [TronUsdt];

    public static CustodyNetwork? Find(string? id) =>
        All.FirstOrDefault(n => string.Equals(n.Id, id, StringComparison.Ordinal));
}
