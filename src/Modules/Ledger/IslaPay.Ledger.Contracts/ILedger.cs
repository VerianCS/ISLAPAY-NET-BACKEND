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

/// <summary>
/// One of IslaPay's own accounts, and what is in it.
/// </summary>
/// <remarks>
/// <para>
/// "The house" is the platform's accounts — fees, the settlement fund,
/// escrow, the float — together with the mirrors of what is held outside, at
/// a bank or on a chain. Customer and merchant accounts are deliberately not
/// here: those are somebody's money, they are read one holder at a time, and
/// there is no screen that wants all of them at once.
/// </para>
/// <para>
/// <paramref name="EntryCount"/> comes from the materialised balance rather
/// than from counting, and it is here because it is what makes the number
/// checkable: a balance that moves while the count does not is a balance that
/// was written by something other than a posting.
/// </para>
/// </remarks>
public sealed record HouseBalance(AccountRef Account, Money Balance, long EntryCount);

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

    /// <summary>
    /// What one account holds.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Zero for an account that has never been opened, because "nothing" and
    /// "no such account" are the same answer to this question and a caller
    /// that had to tell them apart would only get it wrong.
    /// </para>
    /// <para>
    /// This exists for the platform's own accounts. Customer and merchant
    /// accounts may not go negative, so a posting against one is refused if it
    /// would overdraw; platform accounts may, which means nothing in the ledger
    /// stops IslaPay promising money it does not have. A module that can make
    /// such a promise — P2P quoting a payout against the settlement fund — has
    /// to ask first, and this is how.
    /// </para>
    /// </remarks>
    Task<Money> BalanceOfAsync(
        AccountRef account, CancellationToken cancellationToken = default);

    /// <summary>The user's movements across every currency, newest first.</summary>
    Task<LedgerEntryPage> EntriesAsync(
        string userId,
        int limit,
        string? cursor = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Every account IslaPay holds in its own name, and what is in each.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The whole set in one read, because the question it answers is "where is
    /// the money" and that question is not asked one account at a time. Asking
    /// <see cref="BalanceOfAsync"/> in a loop would also need the caller to
    /// already know which accounts exist, and mirrors are named after whatever
    /// rail opened them — so the loop would quietly miss the ones nobody
    /// thought to name.
    /// </para>
    /// <para>
    /// An account appears once it has been opened, at zero if nothing has
    /// touched it yet. Accounts that do not exist are not invented: a row per
    /// currency in the catalogue would fill the screen with zeroes nobody put
    /// there.
    /// </para>
    /// </remarks>
    Task<IReadOnlyList<HouseBalance>> HouseBalancesAsync(
        CancellationToken cancellationToken = default);

    /// <summary>
    /// One account's movements, newest first.
    /// </summary>
    /// <remarks>
    /// <see cref="EntriesAsync"/> answers for a person across every currency
    /// they hold; this answers for a single account, which is the only useful
    /// question about the platform's own, since those have no holder to group
    /// by. An account that has never been opened has no entries rather than
    /// being an error — the same answer <see cref="BalanceOfAsync"/> gives.
    /// </remarks>
    Task<LedgerEntryPage> AccountEntriesAsync(
        AccountRef account,
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

    /// <summary>
    /// The posting recorded under an idempotency key, if there is one.
    /// </summary>
    /// <remarks>
    /// <para>
    /// For callers that keep their own state alongside the ledger's and cannot
    /// write both in one transaction — the ledger owns its transaction, and
    /// handing it out would make every caller a Postgres caller and end any
    /// hope of moving this module into its own process.
    /// </para>
    /// <para>
    /// The window that opens instead is small and always the same shape: the
    /// posting committed and the caller died before recording that it had. A
    /// caller closes it by asking this before it acts, and healing its own
    /// state to match the answer. Doing anything else — a refund, say, for a
    /// hold that was in fact already released — is how money is created.
    /// </para>
    /// </remarks>
    Task<Guid?> FindPostingAsync(
        string idempotencyKey, CancellationToken cancellationToken = default);
}
