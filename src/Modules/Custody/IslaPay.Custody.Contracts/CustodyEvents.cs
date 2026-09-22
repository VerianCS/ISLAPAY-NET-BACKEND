using IslaPay.Platform;

namespace IslaPay.Custody.Contracts;

/// <summary>Routing keys this module publishes under the <c>custody</c> context.</summary>
public static class CustodyEvents
{
    /// <summary>A deposit was seen on the chain and is waiting for depth.</summary>
    public const string DepositDetected = "deposit.detected.v1";

    /// <summary>A deposit reached finality and is in the user's balance.</summary>
    public const string DepositCredited = "deposit.credited.v1";

    /// <summary>A deposit vanished before finality. Nothing was ever posted.</summary>
    public const string DepositOrphaned = "deposit.orphaned.v1";
}

/// <param name="Confirmations">At the moment it was first seen, usually zero or one.</param>
public sealed record DepositDetected(
    Guid DepositId,
    string UserId,
    string Network,
    string TxHash,
    Money Amount,
    int Confirmations,
    DateTimeOffset DetectedAt);

/// <param name="PostingId">The ledger posting that moved it. The audit trail's anchor.</param>
public sealed record DepositCredited(
    Guid DepositId,
    string UserId,
    string Network,
    string TxHash,
    Money Amount,
    Guid PostingId,
    DateTimeOffset CreditedAt);

public sealed record DepositOrphaned(
    Guid DepositId,
    string UserId,
    string Network,
    string TxHash,
    Money Amount,
    DateTimeOffset OrphanedAt);
