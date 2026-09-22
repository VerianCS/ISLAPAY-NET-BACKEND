namespace IslaPay.Platform.AspNet;

/// <summary>
/// What one module believes it is holding in escrow.
/// </summary>
/// <remarks>
/// <para>
/// Escrow is a single platform account per currency, and three modules put
/// money into it: a marketplace hold, a P2P trade, a shop order. The ledger
/// knows the total and knows nothing about why; each module knows its own
/// reasons and cannot see the others'. Neither side can check the other alone.
/// </para>
/// <para>
/// This is the seam that lets somebody check. Each module declares what it
/// thinks it is owed, the treasury adds those up, and the sum is compared with
/// what the ledger actually holds. Agreement means the books are consistent.
/// A gap means either a module lost track of a hold or money left escrow
/// without one — and both are the kind of failure nobody reports, because to
/// the user everything looked fine.
/// </para>
/// <para>
/// <b>Inverted on purpose.</b> The treasury does not reference Marketplace,
/// P2P or Storefront; it takes every implementation the host has registered
/// and does not know what they are. A treasury that imported each module would
/// be a hub every new context had to be wired into, which is the shape this
/// architecture exists to avoid.
/// </para>
/// </remarks>
public interface IEscrowReporter
{
    /// <summary>
    /// The context's own name — <c>marketplace</c>, <c>p2p</c>, <c>storefront</c>.
    /// </summary>
    /// <remarks>
    /// Shown in the breakdown when the figures disagree. "Escrow is short by
    /// 40.00" is an alarm; "the marketplace says 300 and the ledger says 260"
    /// is somewhere to start looking.
    /// </remarks>
    string Context { get; }

    /// <summary>
    /// What this module is still holding, per currency.
    /// </summary>
    /// <remarks>
    /// Currencies with nothing outstanding may be omitted or reported as zero;
    /// the treasury treats the two the same.
    /// </remarks>
    Task<IReadOnlyList<EscrowHolding>> OutstandingAsync(
        CancellationToken cancellationToken = default);
}

/// <summary>
/// One currency's worth of what a module is holding, split by certainty.
/// </summary>
/// <remarks>
/// <para>
/// The split is the whole reason this is not a single number. A module and the
/// ledger commit separately, so at any instant some money is mid-movement: the
/// posting has been asked for and the module does not yet know whether it
/// landed. Counting those as held makes a healthy system look long; counting
/// them as gone makes it look short. Both produce alarms that are not real,
/// and an alarm that cries wolf is worse than no alarm, because it is the one
/// people learn to dismiss.
/// </para>
/// <para>
/// So a reconciliation is a band rather than an equality. The ledger's escrow
/// should sit between <see cref="MinorUnits"/> and
/// <c>MinorUnits + InFlightMinorUnits</c>. Inside the band with nothing in
/// flight is agreement; inside it with something in flight is "ask again in a
/// moment"; outside it is the thing worth waking somebody for.
/// </para>
/// </remarks>
/// <param name="CurrencyCode">
/// The code alone, not a <c>Currency</c>. A reporter reads sums out of its own
/// tables and has no reason to look up a scale to answer a question the
/// treasury will answer with the catalogue anyway.
/// </param>
/// <param name="MinorUnits">
/// Definitely in escrow: the hold posted, nothing has released it. Always
/// positive — escrow holds money on somebody's behalf, and a negative holding
/// is a module reporting a bug rather than a balance.
/// </param>
/// <param name="InFlightMinorUnits">
/// May or may not be in escrow: a posting is mid-flight and the module is
/// waiting to learn which way it went. The sweeper resolves these; a figure
/// that stays here is itself a finding.
/// </param>
public sealed record EscrowHolding(
    string CurrencyCode,
    long MinorUnits,
    long InFlightMinorUnits = 0);
