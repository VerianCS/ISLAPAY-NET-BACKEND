using IslaPay.Platform;

namespace IslaPay.Ledger.Contracts;

/// <summary>A currency the account holder has, and what is in it.</summary>
public sealed record AccountBalance(Currency Currency, Money Balance);

/// <summary>
/// One entry as another module sees it.
/// </summary>
/// <remarks>
/// <paramref name="Kind"/> is the ledger's own classification — what the
/// movement was for, in accounting terms. It is not what the client shows:
/// the app's list has its own vocabulary (<c>transfer_sent</c>,
/// <c>store_purchase</c>) which depends on the sign and on who the reader is.
/// Translating between the two is the Wallet module's job, and keeping them
/// separate is why Wallet exists as something other than a pass-through.
/// </remarks>
/// <param name="Amount">
/// Signed from this account's point of view: positive is money in.
/// </param>
public sealed record LedgerEntryView(
    long Id,
    string Kind,
    Money Amount,
    DateTimeOffset OccurredAt,
    IReadOnlyDictionary<string, string> Metadata);

/// <summary>A page of entries, newest first.</summary>
/// <param name="NextCursor">Opaque; null on the last page.</param>
public sealed record LedgerEntryPage(IReadOnlyList<LedgerEntryView> Items, string? NextCursor);

/// <summary>
/// What the ledger offers the rest of the system.
/// </summary>
/// <remarks>
/// <para>
/// Narrow on purpose, and it grew only when something needed it:
/// <see cref="PostAsync"/> arrived with the first caller that moves money,
/// designed against a real use rather than an imagined one. There is still no
/// method here that a caller does not have.
/// </para>
/// <para>
/// Nothing in this file mentions a table, a connection or SQL, and nothing
/// exposes the ledger's internal domain types. It is the same surface a
/// network API would have, which is the point: if the Ledger is ever lifted
/// into its own service, its callers change transport and not code.
/// </para>
/// </remarks>
public interface ILedger
{
    /// <summary>
    /// Opens the user's accounts if they are not already open.
    /// </summary>
    /// <remarks>
    /// Idempotent, and it has to be: it is called both from the
    /// <c>user.registered</c> event and from the first wallet read, precisely
    /// so that a lost event cannot leave someone without accounts.
    /// </remarks>
    Task EnsureUserAccountsAsync(
        string userId,
        IReadOnlyCollection<Currency> currencies,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// The user's balance in each currency they hold.
    /// </summary>
    /// <remarks>
    /// Includes a currency at zero once the account is open — a wallet that
    /// hides a zero balance looks broken to someone who has just signed up.
    /// </remarks>
    Task<IReadOnlyList<AccountBalance>> BalancesAsync(
        string userId, CancellationToken cancellationToken = default);

    /// <summary>The user's movements across every currency, newest first.</summary>
    Task<LedgerEntryPage> EntriesAsync(
        string userId,
        int limit,
        string? cursor = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Records a transaction, and anything it should announce, as one unit.
    /// </summary>
    /// <remarks>
    /// The legs must sum to zero per currency; a request that does not is
    /// rejected before anything is written. Accounts are opened if they do not
    /// exist, so a first payment to a merchant does not need a separate step.
    /// </remarks>
    /// <exception cref="InsufficientFundsException">
    /// An account that may not go negative would have been overdrawn. Nothing
    /// is written — not the leg that overdrew, and not the ones before it.
    /// </exception>
    Task<PostingReceipt> PostAsync(
        PostingRequest request, CancellationToken cancellationToken = default);
}
