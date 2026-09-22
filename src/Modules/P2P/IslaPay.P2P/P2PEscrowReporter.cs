using IslaPay.P2P.Contracts;
using IslaPay.Platform.AspNet;
using Microsoft.EntityFrameworkCore;

namespace IslaPay.P2P;

/// <summary>
/// What the P2P desk believes it owes in local money, for the treasury.
/// </summary>
/// <remarks>
/// <para>
/// Only sells reach escrow, and only in the local currency. A sell takes the
/// customer's wallet money immediately and puts IslaPay's side — the pesos it
/// now owes them — into escrow until an operator has actually sent the
/// transfer. A buy posts nothing until the operator confirms the money
/// arrived, so a trade awaiting payment is holding nothing and must not be
/// counted; counting it would make escrow look short by the value of every
/// purchase anybody has started and not paid.
/// </para>
/// <para>
/// Answered from the trades table, never from the ledger — a reconciliation
/// that asks the ledger agrees with it by construction and can never find
/// anything.
/// </para>
/// </remarks>
public sealed class P2PEscrowReporter : IEscrowReporter
{
    /// <summary>The operator owes a transfer and has not sent it.</summary>
    private const string Held = P2PTradeStatuses.AwaitingPayout;

    /// <summary>
    /// A posting is mid-flight and this module does not yet know the outcome.
    /// </summary>
    /// <remarks>
    /// <c>pending</c> is a sell whose commit may or may not have landed;
    /// <c>settling</c> is a payout, refund or credit on its way. Reported as
    /// uncertain rather than guessed either way.
    /// </remarks>
    private static readonly string[] InFlight =
    [
        P2PTradeStatuses.Pending,
        P2PTradeStatuses.Settling,
    ];

    private readonly P2PDbContext _db;

    public P2PEscrowReporter(P2PDbContext db)
    {
        ArgumentNullException.ThrowIfNull(db);
        _db = db;
    }

    public string Context => "p2p";

    public async Task<IReadOnlyList<EscrowHolding>> OutstandingAsync(
        CancellationToken cancellationToken = default)
    {
        const string Sell = "sell";

        var rows = await _db.Trades
            .AsNoTracking()
            .Where(t => t.Side == Sell
                        && (t.Status == Held || InFlight.Contains(t.Status)))
            .GroupBy(t => new { t.LocalCurrencyCode, t.Status })
            .Select(g => new
            {
                g.Key.LocalCurrencyCode,
                g.Key.Status,
                Total = g.Sum(t => t.LocalMinor),
            })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return
        [
            .. rows
                .GroupBy(r => r.LocalCurrencyCode, StringComparer.Ordinal)
                .Select(g => new EscrowHolding(
                    g.Key,
                    g.Where(r => r.Status == Held).Sum(r => r.Total),
                    g.Where(r => InFlight.Contains(r.Status)).Sum(r => r.Total))),
        ];
    }
}
