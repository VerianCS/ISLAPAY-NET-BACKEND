using IslaPay.Platform;

namespace IslaPay.Treasury.Contracts;

/// <summary>
/// One of IslaPay's own accounts, as the console shows it.
/// </summary>
/// <param name="Owner">
/// <c>fees</c>, <c>settlement_fund</c>, <c>escrow</c>, <c>float</c> or
/// <c>external</c>. The chart of accounts' own vocabulary (§8.4), not a
/// translated label: what the console displays is the console's business, and
/// a name that differs between the report and the support conversation is
/// worse than a name in English.
/// </param>
/// <param name="Mirror">
/// For an external account, what it mirrors — a chain, a bank, a P2P rail.
/// Null for the platform's own, which have exactly one account per currency.
/// </param>
/// <param name="Balance">
/// Signed as stored. A mirror is negative when money has come in through it:
/// the mirror is what IslaPay holds <em>outside</em>, and money arriving here
/// left there.
/// </param>
/// <param name="Entries">
/// How many postings have touched the account. Not decoration — it is what
/// makes the balance checkable, since a figure that moves while this does not
/// was written by something other than a posting.
/// </param>
public sealed record TreasuryAccountDto(
    string Owner,
    string? Mirror,
    Money Balance,
    long Entries);

/// <summary>Every account IslaPay holds in its own name.</summary>
/// <param name="AsOf">
/// When the figures were read. A balance with no time on it invites somebody
/// to compare this morning's screenshot with this afternoon's total.
/// </param>
public sealed record TreasuryBalancesDto(
    DateTimeOffset AsOf,
    IReadOnlyList<TreasuryAccountDto> Accounts);

/// <summary>What one module says it is holding, in one currency.</summary>
/// <param name="Context">The module's own name — <c>marketplace</c>, <c>p2p</c>.</param>
/// <param name="InFlight">
/// A posting the module has asked for and not yet seen the result of. It may
/// or may not already be in escrow, which is why the check below is a band.
/// </param>
public sealed record EscrowClaimDto(string Context, Money Held, Money InFlight);

/// <summary>
/// One currency's escrow, as the ledger has it and as the modules explain it.
/// </summary>
/// <remarks>
/// <para>
/// The check is deliberately not an equality. A module and the ledger commit
/// separately, so at any instant some money is mid-movement; counting it as
/// held makes a healthy system look long, and counting it as gone makes it
/// look short. Both raise alarms that are not real, and an alarm that cries
/// wolf is the one people learn to dismiss.
/// </para>
/// <para>
/// So <see cref="Ledger"/> should sit between <see cref="Claimed"/> and
/// <see cref="Claimed"/> + the claims' in-flight total. Inside the band with
/// nothing in flight is agreement; inside it with something in flight is "ask
/// again in a moment"; outside it is the thing worth waking somebody for.
/// </para>
/// </remarks>
/// <param name="Ledger">What the escrow account actually holds.</param>
/// <param name="Claimed">What the modules are certain they are holding.</param>
/// <param name="Difference">
/// <see cref="Ledger"/> minus the nearest edge of the band, so zero means
/// agreement and the sign says which way it is wrong. Positive is escrow
/// holding money no module has a reason for; negative is a module expecting
/// money that is not there.
/// </param>
public sealed record EscrowReconciliationDto(
    string Currency,
    Money Ledger,
    Money Claimed,
    Money InFlight,
    Money Difference,
    bool Balanced,
    IReadOnlyList<EscrowClaimDto> Claims);

/// <summary>The escrow check across every currency that has one.</summary>
/// <param name="Balanced">
/// True only when every currency is. One screen, one light: a console that
/// makes somebody scan a table to find out whether anything is wrong is a
/// console nobody checks.
/// </param>
public sealed record TreasuryReconciliationDto(
    DateTimeOffset AsOf,
    bool Balanced,
    IReadOnlyList<EscrowReconciliationDto> Currencies);

/// <summary>
/// Money entering the system.
/// </summary>
/// <remarks>
/// <para>
/// The counter-entry is a mirror account, not thin air: the float rises
/// because cash arrived somewhere real, and <see cref="Source"/> names where.
/// A credit with no counterparty would be a system that can print money and a
/// balance sheet that cannot be audited against a bank statement.
/// </para>
/// <para>
/// The request carries no author. Who did it is taken from the token, the same
/// rule every other endpoint follows — a field for it would be a field that
/// can be filled in with somebody else's name.
/// </para>
/// </remarks>
/// <param name="Destination">
/// <c>float</c> — cash at bank or custodian — or <c>settlement_fund</c>, the
/// pool the exchange and P2P pay out of.
/// </param>
/// <param name="Source">
/// The mirror the money came from: <c>bank:bandec</c>, <c>capital</c>. Free
/// text on purpose, because the rails IslaPay banks with are not something
/// this module can enumerate — but it is required, and it is what a
/// reconciliation against a real statement is done by.
/// </param>
/// <param name="Reason">
/// Why, in words, for whoever reads the ledger in six months. Required: an
/// unexplained credit is indistinguishable from a mistake.
/// </param>
public sealed record CreditRequest(
    string Destination,
    Money Amount,
    string Source,
    string Reason);

/// <param name="Applied">
/// False when an earlier call with the same idempotency key already recorded
/// it. The caller has succeeded either way; this only says whether money moved
/// now.
/// </param>
public sealed record CreditReceiptDto(
    Guid PostingId,
    string Destination,
    Money Amount,
    Money BalanceAfter,
    string Source,
    string Reason,
    string By,
    DateTimeOffset At,
    bool Applied);
