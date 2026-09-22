using IslaPay.Platform;

namespace IslaPay.Ledger.Domain;

/// <summary>What a transaction was for. Reporting and reconciliation read this.</summary>
public enum TransactionKind
{
    Transfer,
    Conversion,
    Payment,
    Payroll,
    Fee,
    Settlement,

    /// <summary>
    /// Money entering the system from outside, put there on purpose.
    /// </summary>
    /// <remarks>
    /// Distinct from <see cref="Settlement"/>, which is a customer's deposit
    /// arriving on a rail, because the two are audited by different people
    /// against different documents: a settlement is reconciled against a chain
    /// or a payment network, and a funding against a bank statement and
    /// somebody's decision to move the money. Filing both under one kind would
    /// make the report that has to find every capital injection unable to.
    /// </remarks>
    Funding,
}

/// <summary>One leg of a transaction: an amount against an account.</summary>
/// <param name="Account">Where it lands.</param>
/// <param name="Amount">
/// Signed. Positive credits the account, negative debits it. The currency is
/// carried by the amount and must match the account's.
/// </param>
public readonly record struct Leg(AccountId Account, Money Amount);

/// <summary>
/// A balanced set of legs, posted as one unit.
/// </summary>
/// <remarks>
/// The invariant that matters is <see cref="Legs"/> summing to zero **per
/// currency** (§8.3.1). It is checked in the constructor, so an unbalanced
/// transaction cannot be represented at all — not merely rejected on the way
/// to the database. Money cannot appear or vanish because there is no way to
/// express it doing so.
/// <para>
/// Per currency, not overall: a conversion touches two currencies and each
/// side balances independently. Summing across currencies would let 100 E-ISLA
/// "balance" against 100 USDT, which is not balance, it is a guess about the
/// rate.
/// </para>
/// </remarks>
public sealed class Posting
{
    public Posting(
        Guid id,
        TransactionKind kind,
        IReadOnlyList<Leg> legs,
        DateTimeOffset postedAt,
        string? idempotencyKey = null,
        Guid? correlationId = null,
        IReadOnlyDictionary<string, string>? metadata = null)
    {
        ArgumentNullException.ThrowIfNull(legs);

        if (legs.Count < 2)
        {
            throw new UnbalancedPostingException(
                "A posting needs at least two legs; one leg cannot balance.");
        }

        foreach (var leg in legs)
        {
            if (leg.Amount.Currency != leg.Account.Currency)
            {
                throw new CurrencyMismatchException(
                    $"Leg posts {leg.Amount.Currency.Code} to {leg.Account}, " +
                    "which is not that account's currency.");
            }

            if (leg.Amount.IsZero)
            {
                throw new UnbalancedPostingException(
                    $"Leg against {leg.Account} is zero. A posting that moves nothing " +
                    "should not exist; it only makes history harder to read.");
            }
        }

        foreach (var group in legs.GroupBy(l => l.Amount.Currency))
        {
            var sum = group.Aggregate(
                Money.Zero(group.Key),
                (running, leg) => running + leg.Amount);

            if (!sum.IsZero)
            {
                throw new UnbalancedPostingException(
                    $"{group.Key.Code} legs sum to {sum}, not zero. " +
                    "Every currency must balance within a posting.");
            }
        }

        Id = id;
        Kind = kind;
        Legs = [.. legs];
        PostedAt = postedAt;
        IdempotencyKey = idempotencyKey;
        CorrelationId = correlationId;
        Metadata = metadata ?? new Dictionary<string, string>();
    }

    public Guid Id { get; }
    public TransactionKind Kind { get; }
    public IReadOnlyList<Leg> Legs { get; }
    public DateTimeOffset PostedAt { get; }

    /// <summary>
    /// Guards against double posting. A second posting with the same key
    /// returns the first one's result rather than moving money again.
    /// </summary>
    public string? IdempotencyKey { get; }

    public Guid? CorrelationId { get; }
    public IReadOnlyDictionary<string, string> Metadata { get; }

    /// <summary>Currencies this posting touches.</summary>
    public IEnumerable<Currency> Currencies => Legs.Select(l => l.Amount.Currency).Distinct();

    /// <summary>The accounts it touches, each once.</summary>
    public IEnumerable<AccountId> Accounts => Legs.Select(l => l.Account).Distinct();
}

/// <summary>A posting whose legs do not sum to zero in some currency.</summary>
public sealed class UnbalancedPostingException(string message) : Exception(message);

/// <summary>A leg whose amount is in a different currency from its account.</summary>
public sealed class CurrencyMismatchException(string message) : Exception(message);

/// <summary>
/// A posting that would take an account that may not go negative below zero.
/// </summary>
public sealed class InsufficientFundsException(AccountId account, Money available, Money requested)
    : Exception($"{account} holds {available}, which does not cover {requested}.")
{
    public AccountId Account { get; } = account;
    public Money Available { get; } = available;
    public Money Requested { get; } = requested;
}
