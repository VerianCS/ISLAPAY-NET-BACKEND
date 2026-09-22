using IslaPay.Marketplace.Contracts;
using IslaPay.Platform.AspNet;
using Microsoft.EntityFrameworkCore;

namespace IslaPay.Marketplace;

/// <summary>
/// What the marketplace believes it is holding, for the treasury to check.
/// </summary>
/// <remarks>
/// <para>
/// It answers from the orders table and never from the ledger. That is the
/// point: if it asked the ledger it would agree with the ledger by
/// construction, and a reconciliation that cannot disagree is a reconciliation
/// that cannot find anything.
/// </para>
/// <para>
/// A buyer's money is in escrow from the moment a hold posts until it is
/// released to the seller or refunded. <c>held</c> is the settled case.
/// <c>pending</c>, <c>releasing</c> and <c>refunding</c> are the three moments
/// where a posting has been asked for and this module does not yet know the
/// answer — reported in flight rather than guessed, because guessing either
/// way produces a discrepancy that is not real.
/// </para>
/// </remarks>
public sealed class MarketplaceEscrowReporter : IEscrowReporter
{
    private static readonly string[] Held = [OrderStatuses.Held];

    private static readonly string[] InFlight =
    [
        OrderStatuses.Pending,
        OrderStatuses.Releasing,
        OrderStatuses.Refunding,
    ];

    private readonly MarketplaceDbContext _db;

    public MarketplaceEscrowReporter(MarketplaceDbContext db)
    {
        ArgumentNullException.ThrowIfNull(db);
        _db = db;
    }

    public string Context => "marketplace";

    public async Task<IReadOnlyList<EscrowHolding>> OutstandingAsync(
        CancellationToken cancellationToken = default)
    {
        // One grouped read rather than two, so the two figures cannot be taken
        // from either side of a hold that settled in between.
        var rows = await _db.Orders
            .AsNoTracking()
            .Where(o => Held.Contains(o.Status) || InFlight.Contains(o.Status))
            .GroupBy(o => new { o.CurrencyCode, o.Status })
            .Select(g => new
            {
                g.Key.CurrencyCode,
                g.Key.Status,
                Total = g.Sum(o => o.AmountMinor),
            })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return
        [
            .. rows
                .GroupBy(r => r.CurrencyCode, StringComparer.Ordinal)
                .Select(g => new EscrowHolding(
                    g.Key,
                    g.Where(r => Held.Contains(r.Status)).Sum(r => r.Total),
                    g.Where(r => InFlight.Contains(r.Status)).Sum(r => r.Total))),
        ];
    }
}
