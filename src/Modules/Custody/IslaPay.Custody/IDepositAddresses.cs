using IslaPay.Catalog.Contracts;
using IslaPay.Custody.Contracts;

namespace IslaPay.Custody;

/// <summary>An address, and whatever the custodian needs to find its key.</summary>
public sealed record IssuedAddress(string Address, string CustodianRef);

/// <summary>
/// Where deposit addresses come from.
/// </summary>
/// <remarks>
/// <para>
/// A port with no production implementation, on purpose, and the same shape as
/// <c>IOtpSender</c>: the module is finished and the thing behind it is not.
/// What sits here in production is a custodian or a key-management service,
/// and the difference between those two is the difference between IslaPay
/// holding customers' private keys and not.
/// </para>
/// <para>
/// That is not a decision to make by writing an implementation and seeing
/// whether anyone objects. Deriving keys in this process would mean the
/// application's memory, its crash dumps, its logs and its backups all become
/// places a private key can leak from, and it would put the answer to "who can
/// move the customers' money" in a file somebody can read. So this interface
/// exists, the host refuses to start without one outside Development, and
/// nothing in this module knows or cares which answer is behind it.
/// </para>
/// </remarks>
public interface IDepositAddresses
{
    /// <summary>
    /// Issues an address for one user on one network.
    /// </summary>
    /// <remarks>
    /// Called once per user, asset and chain — the result is stored and reused. An
    /// implementation may return the same address for the same arguments, and
    /// several will, but nothing here depends on that.
    /// </remarks>
    /// <exception cref="CustodyException">
    /// With <see cref="CustodyErrors.AddressUnavailable"/> when the custodian
    /// cannot be reached or will not issue one.
    /// </exception>
    Task<IssuedAddress> IssueAsync(
        string userId, CurrencyOnNetwork network, CancellationToken cancellationToken = default);
}

/// <summary>
/// Addresses good enough to develop against and worthless anywhere else.
/// </summary>
/// <remarks>
/// <para>
/// Deterministic, so the same developer sees the same address between
/// restarts, and valid for the network's format, so the address check in
/// <c>CustodyService</c> is exercised rather than routed around. It is
/// otherwise made up: there is no key behind it, so money sent to one of
/// these is gone.
/// </para>
/// <para>
/// Registered only in Development, and the host refuses to start without a
/// real implementation anywhere else. The alternative — a fake that quietly
/// ships — is how somebody ends up publishing an address nobody holds.
/// </para>
/// </remarks>
public sealed class DevelopmentDepositAddresses : IDepositAddresses
{
    public Task<IssuedAddress> IssueAsync(
        string userId, CurrencyOnNetwork network, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(network);

        // SHA-512 rather than SHA-256 because the body is 33 characters and
        // a 32-byte digest is one short — which the tests found immediately,
        // and which says something useful about how little this is a real
        // address-derivation scheme.
        var seed = System.Security.Cryptography.SHA512.HashData(
            System.Text.Encoding.UTF8.GetBytes($"{network.NetworkId}:{userId}"));

        // Base58, and none of its lookalike characters, so what comes out
        // satisfies the network's own pattern.
        const string Alphabet = "123456789ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnopqrstuvwxyz";
        var body = new char[33];
        for (var i = 0; i < body.Length; i++)
        {
            body[i] = Alphabet[seed[i] % Alphabet.Length];
        }

        return Task.FromResult(
            new IssuedAddress($"T{new string(body)}", $"dev:{Convert.ToHexString(seed)[..16]}"));
    }
}
