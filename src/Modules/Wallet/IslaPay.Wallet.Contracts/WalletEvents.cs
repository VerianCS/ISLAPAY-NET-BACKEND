using IslaPay.Platform;

namespace IslaPay.Wallet.Contracts;

/// <summary>What Wallet publishes, and where.</summary>
public static class WalletEvents
{
    /// <summary>The bounded context, and therefore the exchange <c>x.wallet</c>.</summary>
    public const string Context = "wallet";

    /// <summary>Money moved between two IslaPay accounts.</summary>
    public const string TransferCompleted = "transfer.completed.v1";
}

/// <summary>
/// A transfer that has been recorded in the ledger.
/// </summary>
/// <remarks>
/// Published in the same transaction as the entries, so it cannot describe a
/// movement that did not happen. Consumers — notifications, statements, fraud
/// review — read this rather than the ledger, and none of them needs to know
/// that the ledger exists.
/// </remarks>
/// <param name="PostingId">Ties the event to the entries it describes.</param>
/// <param name="Amount">Positive. The direction is in the two user ids.</param>
public sealed record TransferCompleted(
    Guid PostingId,
    string FromUserId,
    string ToUserId,
    Money Amount,
    DateTimeOffset OccurredAt);
