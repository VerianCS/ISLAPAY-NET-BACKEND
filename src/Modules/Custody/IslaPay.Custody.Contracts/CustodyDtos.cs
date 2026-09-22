using IslaPay.Platform;

namespace IslaPay.Custody.Contracts;

/// <summary>
/// <c>GET /v1/me/custody/addresses/{network}</c> — where to send money.
/// </summary>
/// <remarks>
/// One address per user, network and currency, kept forever rather than
/// rotated. A fresh address per deposit is better for the depositor's privacy
/// and worse for everything else: somebody who saves the address and sends to
/// it a second time — which people do — would be sending to an address
/// IslaPay has stopped watching.
/// </remarks>
/// <param name="Network">A <see cref="CustodyNetworks"/> id.</param>
/// <param name="NetworkName">What to show beside it, e.g. <c>TRON (TRC-20)</c>.</param>
/// <param name="Currency">Wire code of the only currency this address accepts.</param>
/// <param name="Address">
/// Checked against the network's format before it was stored. Show it
/// verbatim, and never let a client reformat it.
/// </param>
/// <param name="Confirmations">
/// How many blocks a deposit waits before it is credited. The client shows it
/// as "available after N confirmations", which is the difference between a
/// wait somebody understands and one that looks like money has vanished.
/// </param>
public sealed record DepositAddressDto(
    string Network,
    string NetworkName,
    string Currency,
    string Address,
    int Confirmations);

/// <summary>Where a deposit is in its journey.</summary>
public static class DepositStatuses
{
    /// <summary>Seen on the chain, not yet deep enough to be believed.</summary>
    public const string Confirming = "confirming";

    /// <summary>
    /// Final, and the credit is in flight.
    /// </summary>
    /// <remarks>
    /// The window between the ledger committing and this module recording that
    /// it did. A deposit is never left here: the sweeper asks the ledger what
    /// actually happened and finishes it.
    /// </remarks>
    public const string Crediting = "crediting";

    /// <summary>In the user's balance.</summary>
    public const string Credited = "credited";

    /// <summary>
    /// Disappeared from the chain before it was final.
    /// </summary>
    /// <remarks>
    /// A reorganisation, which is a normal event at shallow depth and the
    /// whole reason nothing is credited before finality. Nothing was posted,
    /// so there is nothing to reverse — the row stays only so the user is not
    /// left wondering where the deposit they watched went.
    /// </remarks>
    public const string Orphaned = "orphaned";
}

/// <summary>One deposit, as the app shows it.</summary>
/// <param name="TxHash">
/// The chain's own identifier. Shown so somebody can look it up in a block
/// explorer, which is the only way a user can verify us rather than trust us.
/// </param>
/// <param name="Confirmations">
/// Where it is now, against <paramref name="RequiredConfirmations"/>.
/// </param>
/// <param name="CreditedAt">Null until it is in the balance.</param>
public sealed record DepositDto(
    string Id,
    string Network,
    string Address,
    string TxHash,
    Money Amount,
    string Status,
    int Confirmations,
    int RequiredConfirmations,
    DateTimeOffset FirstSeenAt,
    DateTimeOffset? CreditedAt);
