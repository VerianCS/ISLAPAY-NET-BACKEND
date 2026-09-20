using IslaPay.Platform;

namespace IslaPay.Ledger.Domain;

/// <summary>One posted leg, as it is recorded.</summary>
/// <param name="Sequence">
/// Monotonic and gapless per account (§8.2), so a gap is detectable evidence
/// that something was removed.
/// </param>
public readonly record struct Entry(
    long Sequence,
    Guid PostingId,
    AccountId Account,
    Money Amount,
    Money RunningBalance,
    DateTimeOffset PostedAt);

/// <summary>
/// A double-entry ledger, in memory.
/// </summary>
/// <remarks>
/// <para>
/// This is the reference implementation of the rules in §8.3 — the durable one
/// will enforce the same things in PostgreSQL with deferred constraints and
/// restricted grants. Keeping them here too is not duplication: it is what
/// lets the rules be property-tested over thousands of generated sequences in
/// milliseconds, which is not something a database round trip allows.
/// </para>
/// <para>
/// Three rules hold at all times, and the tests state them as properties
/// rather than as examples:
/// </para>
/// <list type="number">
///   <item>Every posting balances to zero per currency — enforced by
///   <see cref="Posting"/> itself, so it cannot even be constructed otherwise.</item>
///   <item>Entries are append-only. There is no method here that edits or
///   removes one; a mistake is undone by posting its reverse, which leaves
///   both the error and the correction visible.</item>
///   <item>A user or merchant account may not go negative.</item>
/// </list>
/// <para>
/// And the consequence that makes the whole thing worth doing: a balance is
/// never stored as a fact of its own. It is a fold over entries, so it cannot
/// drift from them.
/// </para>
/// </remarks>
public sealed class Ledger
{
    private readonly List<Entry> _entries = [];
    private readonly Dictionary<AccountId, Money> _balances = [];
    private readonly Dictionary<AccountId, long> _sequences = [];
    private readonly Dictionary<string, Guid> _idempotency = [];
    private readonly Dictionary<Guid, Posting> _postings = [];

    /// <summary>Every entry, in the order it was posted.</summary>
    public IReadOnlyList<Entry> Entries => _entries;

    /// <summary>Postings by id, for receipts and reversal.</summary>
    public IReadOnlyDictionary<Guid, Posting> Postings => _postings;

    /// <summary>
    /// The balance of an account: the sum of its entries, nothing else.
    /// </summary>
    public Money BalanceOf(AccountId account) =>
        _balances.TryGetValue(account, out var balance)
            ? balance
            : Money.Zero(account.Currency);

    /// <summary>Entries for one account, oldest first.</summary>
    public IReadOnlyList<Entry> EntriesFor(AccountId account) =>
        [.. _entries.Where(e => e.Account == account)];

    /// <summary>
    /// Posts a transaction, or returns the previous result if this
    /// idempotency key has already been used.
    /// </summary>
    /// <returns>
    /// The entries created. On an idempotent replay, the entries the original
    /// posting created — the same result, without moving money twice.
    /// </returns>
    /// <exception cref="InsufficientFundsException">
    /// An account that may not go negative would be taken below zero. Checked
    /// across the whole posting before anything is written, so a rejected
    /// posting leaves no trace at all.
    /// </exception>
    public IReadOnlyList<Entry> Post(Posting posting)
    {
        ArgumentNullException.ThrowIfNull(posting);

        if (posting.IdempotencyKey is { } key &&
            _idempotency.TryGetValue(key, out var existing))
        {
            return [.. _entries.Where(e => e.PostingId == existing)];
        }

        // Compute every resulting balance first. A posting is atomic: it
        // applies completely or not at all, and discovering the third leg
        // overdraws an account after writing the first two would leave the
        // ledger in a state no rule allows.
        var projected = new Dictionary<AccountId, Money>();
        foreach (var leg in posting.Legs)
        {
            var current = projected.TryGetValue(leg.Account, out var seen)
                ? seen
                : BalanceOf(leg.Account);
            projected[leg.Account] = current + leg.Amount;
        }

        foreach (var (account, balance) in projected)
        {
            if (balance.IsNegative && !account.MayGoNegative)
            {
                throw new InsufficientFundsException(
                    account,
                    BalanceOf(account),
                    -(balance - BalanceOf(account)));
            }
        }

        var created = new List<Entry>(posting.Legs.Count);
        foreach (var leg in posting.Legs)
        {
            var sequence = _sequences.GetValueOrDefault(leg.Account) + 1;
            _sequences[leg.Account] = sequence;

            var balance = BalanceOf(leg.Account) + leg.Amount;
            _balances[leg.Account] = balance;

            var entry = new Entry(
                Sequence: sequence,
                PostingId: posting.Id,
                Account: leg.Account,
                Amount: leg.Amount,
                RunningBalance: balance,
                PostedAt: posting.PostedAt);

            _entries.Add(entry);
            created.Add(entry);
        }

        _postings[posting.Id] = posting;
        if (posting.IdempotencyKey is { } newKey) _idempotency[newKey] = posting.Id;

        return created;
    }

    /// <summary>
    /// Builds the posting that undoes <paramref name="postingId"/>.
    /// </summary>
    /// <remarks>
    /// The only way to correct a mistake. Entries are never edited or deleted,
    /// so history shows both what happened and what was done about it — which
    /// is the difference between a ledger and a spreadsheet.
    /// </remarks>
    public Posting BuildReversal(Guid postingId, Guid reversalId, DateTimeOffset at)
    {
        if (!_postings.TryGetValue(postingId, out var original))
        {
            throw new InvalidOperationException($"No posting {postingId} to reverse.");
        }

        return new Posting(
            id: reversalId,
            kind: original.Kind,
            legs: [.. original.Legs.Select(l => new Leg(l.Account, -l.Amount))],
            postedAt: at,
            correlationId: original.CorrelationId,
            metadata: new Dictionary<string, string>
            {
                ["reverses"] = postingId.ToString(),
            });
    }

    /// <summary>
    /// Sums every entry per currency. Should always be zero.
    /// </summary>
    /// <remarks>
    /// The whole-ledger version of the per-posting invariant, and the check a
    /// nightly reconciliation job runs. If this is ever non-zero, money was
    /// created or destroyed and everything downstream is wrong.
    /// </remarks>
    public IReadOnlyDictionary<Currency, Money> TotalsByCurrency()
    {
        var totals = new Dictionary<Currency, Money>();
        foreach (var entry in _entries)
        {
            var currency = entry.Amount.Currency;
            totals[currency] = totals.TryGetValue(currency, out var running)
                ? running + entry.Amount
                : entry.Amount;
        }
        return totals;
    }
}
