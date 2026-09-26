using IslaPay.Platform;

namespace IslaPay.Ledger.Contracts;

/// <summary>Whose account a leg lands in.</summary>
/// <remarks>
/// A closed set, not a free-text name. The chart of accounts (§8.4) is the
/// vocabulary of every reconciliation report and every support conversation,
/// and a typo in a free-text account name is a posting that silently goes
/// somewhere else.
/// </remarks>
public enum AccountOwner
{
    User,
    Merchant,

    /// <summary>Fee revenue.</summary>
    Fees,

    /// <summary>The exchange fund the app shows.</summary>
    SettlementFund,

    /// <summary>Funds held for P2P and partner bookings.</summary>
    Escrow,

    /// <summary>Cash at bank or custodian.</summary>
    CashFloat,

    /// <summary>Mirror of something held outside — a chain, a bank.</summary>
    External,

    /// <summary>
    /// Where E-ISLA comes from and goes back to.
    /// </summary>
    /// <remarks>
    /// Negative by exactly the E-ISLA in circulation: minting moves some from
    /// here to a platform account, burning moves it back. Nothing else posts
    /// to it, so its balance is the supply, read in one place.
    /// </remarks>
    Issuer,
}

/// <summary>
/// An account, named without reference to the ledger's internals.
/// </summary>
/// <remarks>
/// The whole point of this type. A caller has to be able to say "the user's
/// E-ISLA account" without linking against <c>IslaPay.Ledger.Domain</c>, or the
/// public face of the module leaks its inside and the seam stops meaning
/// anything.
/// </remarks>
/// <param name="Id">
/// The user or merchant id, or the mirror's name. Ignored — and must be empty
/// — for the platform's own accounts, which have one per currency.
/// </param>
public sealed record AccountRef(AccountOwner Owner, string Id, Currency Currency)
{
    public static AccountRef User(string userId, Currency currency) =>
        new(AccountOwner.User, Require(userId), currency);

    public static AccountRef Merchant(string merchantId, Currency currency) =>
        new(AccountOwner.Merchant, Require(merchantId), currency);

    public static AccountRef Fees(Currency currency) =>
        new(AccountOwner.Fees, string.Empty, currency);

    public static AccountRef SettlementFund(Currency currency) =>
        new(AccountOwner.SettlementFund, string.Empty, currency);

    public static AccountRef Escrow(Currency currency) =>
        new(AccountOwner.Escrow, string.Empty, currency);

    public static AccountRef CashFloat(Currency currency) =>
        new(AccountOwner.CashFloat, string.Empty, currency);

    public static AccountRef Issuer(Currency currency) =>
        new(AccountOwner.Issuer, string.Empty, currency);

    public static AccountRef External(string mirror, Currency currency) =>
        new(AccountOwner.External, Require(mirror), currency);

    private static string Require(string value) =>
        string.IsNullOrWhiteSpace(value)
            ? throw new ArgumentException("An account owner cannot be blank.", nameof(value))
            : value;
}

/// <summary>One side of a transaction.</summary>
/// <param name="Amount">
/// Signed. Positive credits the account, negative debits it. The currency is
/// carried by the amount and must match the account's.
/// </param>
public sealed record PostingLeg(AccountRef Account, Money Amount);

/// <summary>
/// A fact to announce, committed with the posting rather than after it.
/// </summary>
/// <remarks>
/// <para>
/// The ledger does not interpret these — it writes them to the outbox inside
/// the same transaction as the entries. That is the only way the event and the
/// money it describes cannot disagree: enqueuing afterwards is a second write
/// that can fail on its own, and an announced transfer that did not happen is
/// worse than one that happened quietly.
/// </para>
/// <para>
/// <paramref name="Payload"/> is serialised with the platform's own JSON
/// settings, so what lands on the wire is what a consumer's contract test
/// asserts.
/// </para>
/// </remarks>
public sealed record PendingEvent(string Context, string RoutingKey, object Payload);

/// <summary>
/// A balanced set of legs, and whatever should be announced with it.
/// </summary>
/// <param name="Kind">
/// The ledger's own classification, for reconciliation — <c>transfer</c>,
/// <c>conversion</c>, <c>settlement</c>. Not what the app shows.
/// </param>
/// <param name="IdempotencyKey">
/// A retry of the same user intent must not move money twice. Null repeats
/// freely; a value may be used once.
/// <para>
/// Unique across the whole ledger, not per caller or per user — it is a
/// single unique index. So a caller must qualify it with something that makes
/// it theirs, because a key the client chose is only unique to that client:
/// two users who happen to pick the same one would otherwise collide, and the
/// second would be told their transfer succeeded while no money moved.
/// </para>
/// </param>
/// <param name="PostingId">
/// Assigned by the caller when it needs to name the posting in something it
/// is writing at the same time — an event, or a response. Null lets the ledger
/// choose.
/// </param>
public sealed record PostingRequest(
    string Kind,
    IReadOnlyList<PostingLeg> Legs,
    string? IdempotencyKey = null,
    IReadOnlyDictionary<string, string>? Metadata = null,
    IReadOnlyList<PendingEvent>? Events = null,
    Guid? PostingId = null);

/// <param name="Written">
/// False when an earlier call with the same key already recorded it. The
/// caller has succeeded either way; this only says whether money moved now.
/// </param>
public sealed record PostingReceipt(Guid PostingId, bool Written);

/// <summary>The account does not hold enough, and may not go negative.</summary>
/// <remarks>
/// Carries the figures because the client's <c>InsufficientFunds(currency)</c>
/// cannot be constructed without them — see <c>API_CONTRACT.md</c> §4.
/// </remarks>
public sealed class InsufficientFundsException : Exception
{
    public InsufficientFundsException(AccountRef account, Money available, Money requested)
        : base($"{account} holds {available} and {requested} was requested.")
    {
        Account = account;
        Available = available;
        Requested = requested;
    }

    public AccountRef Account { get; } = null!;

    public Money Available { get; }

    public Money Requested { get; }
}
