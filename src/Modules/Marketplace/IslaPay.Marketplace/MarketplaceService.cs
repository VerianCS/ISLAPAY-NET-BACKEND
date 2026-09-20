using System.Globalization;
using System.Text;
using IslaPay.Identity.Contracts;
using IslaPay.Ledger.Contracts;
using IslaPay.Marketplace.Contracts;
using IslaPay.Platform;
using IslaPay.Platform.Api;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace IslaPay.Marketplace;

/// <summary>
/// Listings, and the money locked against them.
/// </summary>
/// <remarks>
/// <para>
/// The shape of every money movement here is the same, and it is the shape
/// forced by the ledger owning its own transaction. Three steps: claim the
/// order row into an in-flight status, post to the ledger, record what was
/// posted. Two of those are this module's database and the middle one is not,
/// so a process that dies between them leaves the in-flight status behind —
/// which is exactly what <see cref="RepairAsync"/> needs to tell "the money
/// never moved" from "the money moved and nobody wrote it down".
/// </para>
/// <para>
/// One idempotency key, <c>mkt:settle:{orderId}</c>, covers both ways an order
/// can end. That is deliberate and it is the strongest guarantee in this file:
/// releasing to the seller and refunding to the buyer compete for the same
/// unique index in the ledger, so escrow pays out at most once per order even
/// if everything written here is wrong.
/// </para>
/// </remarks>
public sealed class MarketplaceService
{
    private readonly MarketplaceDbContext _db;
    private readonly ILedger _ledger;
    private readonly IUserDirectory _directory;
    private readonly MarketplaceOptions _options;
    private readonly TimeProvider _clock;

    public MarketplaceService(
        MarketplaceDbContext db,
        ILedger ledger,
        IUserDirectory directory,
        MarketplaceOptions options,
        TimeProvider? clock = null)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(ledger);
        ArgumentNullException.ThrowIfNull(directory);
        ArgumentNullException.ThrowIfNull(options);
        _db = db;
        _ledger = ledger;
        _directory = directory;
        _options = options;
        _clock = clock ?? TimeProvider.System;
    }

    /// <summary>The ledger key for moving a buyer's money into escrow.</summary>
    public static string HoldKey(Guid orderId) =>
        $"mkt:hold:{orderId.ToString("D", CultureInfo.InvariantCulture)}";

    /// <summary>
    /// The ledger key for moving it out again — to the seller or back to the
    /// buyer, whichever happens.
    /// </summary>
    /// <remarks>
    /// One key for both on purpose. See the class remarks: it makes a double
    /// payout from escrow impossible at the ledger rather than merely unlikely
    /// at this layer.
    /// </remarks>
    public static string SettleKey(Guid orderId) =>
        $"mkt:settle:{orderId.ToString("D", CultureInfo.InvariantCulture)}";

    // --------------------------------------------------------------- listings

    /// <summary>Puts an item up for sale.</summary>
    /// <remarks>
    /// No phone check. Publishing is not a money movement, and gating it would
    /// leave the market empty to enforce a rule that is enforced anyway at the
    /// moment somebody pays — see D11 and <see cref="MarketplaceOptions"/>.
    /// </remarks>
    public async Task<ListingDto> PublishAsync(
        string sellerId, PublishListingRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sellerId);
        ArgumentNullException.ThrowIfNull(request);

        var title = (request.Title ?? string.Empty).Trim();
        if (title.Length == 0 || title.Length > _options.MaxTitleLength)
        {
            throw new MarketplaceException(
                MarketplaceErrors.InvalidListing, 422,
                $"A listing needs a title of at most {_options.MaxTitleLength} characters.");
        }

        var description = (request.Description ?? string.Empty).Trim();
        if (description.Length > _options.MaxDescriptionLength)
        {
            throw new MarketplaceException(
                MarketplaceErrors.InvalidListing, 422,
                $"A description may be at most {_options.MaxDescriptionLength} characters.");
        }

        var category = (request.Category ?? string.Empty).Trim();
        var condition = (request.Condition ?? string.Empty).Trim();
        if (category.Length == 0 || condition.Length == 0)
        {
            throw new MarketplaceException(
                MarketplaceErrors.InvalidListing, 422,
                "A listing needs a category and a condition.");
        }

        var photos = request.Photos ?? [];
        if (photos.Count > _options.MaxPhotos)
        {
            throw new MarketplaceException(
                MarketplaceErrors.InvalidListing, 422,
                $"A listing may carry at most {_options.MaxPhotos} photos.");
        }

        if (!request.Price.IsPositive)
        {
            throw new MarketplaceException(
                MarketplaceErrors.InvalidPrice, 422, "A listing must be priced above zero.");
        }

        var seller = await _directory.FindByIdAsync(sellerId, cancellationToken).ConfigureAwait(false)
            ?? throw new MarketplaceException(
                MarketplaceErrors.NotTheSeller, 403, "The seller's account no longer exists.");

        var now = _clock.GetUtcNow();
        var listing = new Listing
        {
            Id = Guid.NewGuid(),
            SellerId = sellerId,
            SellerName = seller.Name,
            Title = title,
            Description = description,
            Category = category,
            Condition = condition,
            CurrencyCode = request.Price.Currency.Code(),
            PriceMinor = request.Price.MinorUnits,
            Location = (request.Location ?? string.Empty).Trim(),
            Photos = [.. photos],
            Status = ListingStatuses.Active,
            PublishedAt = now,
            UpdatedAt = now,
        };

        _db.Listings.Add(listing);
        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return Project(listing);
    }

    /// <summary>What is on offer, newest first.</summary>
    public async Task<CursorPage<ListingDto>> BrowseAsync(
        string? category,
        string? search,
        int limit,
        string? cursor,
        CancellationToken cancellationToken = default)
    {
        var query = _db.Listings.AsNoTracking()
            .Where(l => l.Status == ListingStatuses.Active);

        if (!string.IsNullOrWhiteSpace(category))
        {
            query = query.Where(l => l.Category == category.Trim());
        }

        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = $"%{search.Trim()}%";
            query = query.Where(l => EF.Functions.ILike(l.Title, term));
        }

        return await PageAsync(query, limit, cursor, Project, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Everything one seller has listed, in any status.</summary>
    public Task<CursorPage<ListingDto>> ListingsOfAsync(
        string sellerId, int limit, string? cursor, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sellerId);

        return PageAsync(
            _db.Listings.AsNoTracking().Where(l => l.SellerId == sellerId),
            limit, cursor, Project, cancellationToken);
    }

    /// <summary>One listing.</summary>
    public async Task<ListingDto> ListingAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var listing = await _db.Listings.AsNoTracking()
            .FirstOrDefaultAsync(l => l.Id == id, cancellationToken).ConfigureAwait(false);

        return listing is null
            ? throw new MarketplaceException(
                MarketplaceErrors.ListingNotFound, 404, "No such listing.")
            : Project(listing);
    }

    /// <summary>Takes a listing down.</summary>
    /// <remarks>
    /// Refused while somebody has money locked against it. A seller who could
    /// withdraw a reserved item would strand the buyer's balance until the hold
    /// timed out; cancelling the order is the way out, and it refunds.
    /// </remarks>
    public async Task<ListingDto> WithdrawAsync(
        string sellerId, Guid id, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sellerId);

        var listing = await _db.Listings
            .FirstOrDefaultAsync(l => l.Id == id, cancellationToken).ConfigureAwait(false)
            ?? throw new MarketplaceException(
                MarketplaceErrors.ListingNotFound, 404, "No such listing.");

        if (!string.Equals(listing.SellerId, sellerId, StringComparison.Ordinal))
        {
            throw new MarketplaceException(
                MarketplaceErrors.NotTheSeller, 403, "Only the seller may withdraw a listing.");
        }

        if (listing.Status == ListingStatuses.Withdrawn) return Project(listing);

        if (listing.Status != ListingStatuses.Active)
        {
            throw new MarketplaceException(
                MarketplaceErrors.ListingUnavailable, 409,
                listing.Status == ListingStatuses.Reserved
                    ? "Somebody has money locked against this listing. Cancel the order first."
                    : "A sold listing cannot be withdrawn.");
        }

        listing.Status = ListingStatuses.Withdrawn;
        listing.UpdatedAt = _clock.GetUtcNow();
        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return Project(listing);
    }

    // ----------------------------------------------------------------- orders

    /// <summary>
    /// Locks the price of a listing in escrow and issues the code that
    /// releases it.
    /// </summary>
    /// <remarks>
    /// The money genuinely leaves the buyer's account. A "reservation" that
    /// only wrote a row would let the same balance be promised to three
    /// sellers at once, and the first one to scan would be the only one paid.
    /// </remarks>
    public async Task<OrderDto> PlaceOrderAsync(
        string buyerId, Guid listingId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(buyerId);

        var buyer = await _directory.FindByIdAsync(buyerId, cancellationToken).ConfigureAwait(false)
            ?? throw new MarketplaceException(
                MarketplaceErrors.OrderNotFound, 422, "The buyer's account no longer exists.");

        if (_options.RequireVerifiedPhone && !buyer.PhoneVerified)
        {
            throw new MarketplaceException(
                MarketplaceErrors.PhoneNotVerified, 403,
                "The account must prove its phone number before money can move.");
        }

        var order = await ClaimListingAsync(buyerId, buyer.Name, listingId, cancellationToken)
            .ConfigureAwait(false);

        try
        {
            var receipt = await _ledger.PostAsync(
                new PostingRequest(
                    Kind: "payment",
                    Legs:
                    [
                        new PostingLeg(AccountRef.User(buyerId, order.Amount.Currency), -order.Amount),
                        new PostingLeg(AccountRef.Escrow(order.Amount.Currency), order.Amount),
                    ],
                    IdempotencyKey: HoldKey(order.Id),
                    Metadata: new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["orderId"] = order.Id.ToString("D", CultureInfo.InvariantCulture),
                        ["listingId"] = order.ListingId.ToString("D", CultureInfo.InvariantCulture),
                        ["listingTitle"] = order.ListingTitle,
                        ["sellerName"] = order.SellerName,
                    },
                    Events:
                    [
                        new PendingEvent(
                            MarketplaceEvents.Context,
                            MarketplaceEvents.OrderHeld,
                            new OrderHeld(
                                order.Id, order.ListingId, order.BuyerId, order.SellerId,
                                order.Amount, order.ExpiresAt, _clock.GetUtcNow())),
                    ]),
                cancellationToken).ConfigureAwait(false);

            await MarkHeldAsync(order.Id, receipt.PostingId, cancellationToken).ConfigureAwait(false);
            order.Status = OrderStatuses.Held;
            order.HoldPostingId = receipt.PostingId;
        }
        catch (InsufficientFundsException e)
        {
            // A refusal, not a failure. The ledger decided, and it decided
            // before writing anything, so undoing the reservation is safe.
            await AbandonQuietlyAsync(order, cancellationToken).ConfigureAwait(false);

            var failure = new MarketplaceException(
                MarketplaceErrors.InsufficientFunds, 422,
                $"The account holds {e.Available} and {e.Requested} was requested.");
            failure.Facts["currency"] = order.Amount.Currency.Code();
            failure.Facts["available"] = e.Available.ToString();
            failure.Facts["requested"] = e.Requested.ToString();
            throw failure;
        }
        catch
        {
            // Anything else — the ledger unreachable, a socket closing while
            // the response came back, the process shutting down. The posting
            // may well have committed, so the reservation is undone only if
            // the ledger confirms that nothing was written. If it does not, or
            // cannot be asked, the row is left pending for the sweeper, which
            // can tell the two apart later. Deleting an order whose money is
            // already in escrow strands that money with nothing pointing at it.
            await AbandonIfNothingPostedAsync(order, cancellationToken).ConfigureAwait(false);
            throw;
        }

        return Project(order, forBuyer: true);
    }

    /// <summary>
    /// The seller scanned the buyer's code: escrow pays out.
    /// </summary>
    /// <param name="sellerId">
    /// From the token. A code is worthless without it — which is the whole
    /// mechanism: the buyer decides when to hand the code over, and only the
    /// account that listed the item can turn it into money.
    /// </param>
    public async Task<OrderDto> RedeemAsync(
        string sellerId, string? code, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sellerId);

        var normalised = RedemptionCode.Normalise(code)
            ?? throw CodeInvalid();

        var order = await ClaimForReleaseAsync(sellerId, normalised, cancellationToken)
            .ConfigureAwait(false);

        if (order.Status == OrderStatuses.Released) return Project(order, forBuyer: false);

        var postingId = await PostSettlementAsync(order, refundReason: null, cancellationToken)
            .ConfigureAwait(false);
        await FinishSettlementAsync(order, postingId, cancellationToken).ConfigureAwait(false);

        return Project(order, forBuyer: false);
    }

    /// <summary>Calls the sale off and returns the money to the buyer.</summary>
    /// <remarks>
    /// Either party may. The seller because the item turned out to be gone;
    /// the buyer because they changed their mind before handing the code over.
    /// Once the seller has scanned, neither can: the goods have changed hands
    /// and a refund is a dispute, which this build does not have.
    /// </remarks>
    public async Task<OrderDto> CancelAsync(
        string actorId, Guid orderId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(actorId);

        var order = await ClaimForRefundAsync(actorId, orderId, cancellationToken)
            .ConfigureAwait(false);

        if (OrderStatuses.Terminal.Contains(order.Status))
        {
            return Project(order, forBuyer: IsBuyer(order, actorId));
        }

        var postingId = await PostSettlementAsync(order, OrderRefundReasons.Cancelled, cancellationToken)
            .ConfigureAwait(false);
        await FinishSettlementAsync(order, postingId, cancellationToken).ConfigureAwait(false);

        return Project(order, forBuyer: IsBuyer(order, actorId));
    }

    /// <summary>One order, for whichever of the two parties is asking.</summary>
    public async Task<OrderDto> OrderAsync(
        string callerId, Guid orderId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(callerId);

        var order = await _db.Orders.AsNoTracking()
            .FirstOrDefaultAsync(o => o.Id == orderId, cancellationToken).ConfigureAwait(false);

        // One answer for "no such order" and "not yours". A different status
        // code for the second would confirm that an id exists.
        if (order is null || (!IsBuyer(order, callerId) && !IsSeller(order, callerId)))
        {
            throw new MarketplaceException(
                MarketplaceErrors.OrderNotFound, 404, "No such order.");
        }

        return Project(order, IsBuyer(order, callerId));
    }

    /// <summary>The caller's orders, as buyer or as seller.</summary>
    public Task<CursorPage<OrderDto>> OrdersOfAsync(
        string callerId,
        bool asSeller,
        int limit,
        string? cursor,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(callerId);

        var query = _db.Orders.AsNoTracking()
            .Where(o => asSeller ? o.SellerId == callerId : o.BuyerId == callerId);

        return PageAsync(query, limit, cursor, o => Project(o, forBuyer: !asSeller), cancellationToken);
    }

    // ------------------------------------------------------------- the repair

    /// <summary>
    /// Returns holds that have run out of time, and finishes movements that
    /// were interrupted.
    /// </summary>
    /// <remarks>
    /// Driven by <see cref="HoldSweeper"/>. Every decision it makes is checked
    /// against the ledger rather than inferred from the order row, because the
    /// order row is precisely the thing that may be out of date.
    /// </remarks>
    /// <returns>How many orders it touched.</returns>
    public async Task<int> RepairAsync(CancellationToken cancellationToken = default)
    {
        var now = _clock.GetUtcNow();
        var staleBefore = now - _options.InFlightGrace;
        var touched = 0;

        var candidates = await _db.Orders.AsNoTracking()
            .Where(o =>
                (o.Status == OrderStatuses.Held && o.ExpiresAt <= now)
                || ((o.Status == OrderStatuses.Pending
                        || o.Status == OrderStatuses.Releasing
                        || o.Status == OrderStatuses.Refunding)
                    && o.CreatedAt <= staleBefore))
            .OrderBy(o => o.Seq)
            .Take(100)
            .Select(o => o.Id)
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        foreach (var id in candidates)
        {
            if (await RepairOneAsync(id, cancellationToken).ConfigureAwait(false)) touched++;
        }

        return touched;
    }

    private async Task<bool> RepairOneAsync(Guid id, CancellationToken cancellationToken)
    {
        // Re-read under a lock: the row may have been settled by a request
        // between the scan above and now, which is the common case rather than
        // the exotic one.
        var order = await _db.Orders
            .FirstOrDefaultAsync(o => o.Id == id, cancellationToken).ConfigureAwait(false);
        if (order is null) return false;

        switch (order.Status)
        {
            case OrderStatuses.Pending:
                {
                    var posted = await _ledger.FindPostingAsync(HoldKey(order.Id), cancellationToken)
                        .ConfigureAwait(false);

                    if (posted is { } holdId)
                    {
                        // The hold committed and the request died before saying so.
                        await MarkHeldAsync(order.Id, holdId, cancellationToken).ConfigureAwait(false);
                    }
                    else
                    {
                        // No money ever moved. Free the listing and forget the row.
                        await AbandonAsync(order, cancellationToken).ConfigureAwait(false);
                    }

                    return true;
                }

            case OrderStatuses.Held when order.ExpiresAt <= _clock.GetUtcNow():
                {
                    var claimed = await ClaimForRefundAsync(
                        order.BuyerId, order.Id, OrderRefundReasons.Expired, cancellationToken)
                        .ConfigureAwait(false);

                    if (OrderStatuses.Terminal.Contains(claimed.Status)) return false;

                    var postingId = await PostSettlementAsync(
                        claimed, OrderRefundReasons.Expired, cancellationToken).ConfigureAwait(false);
                    await FinishSettlementAsync(claimed, postingId, cancellationToken)
                        .ConfigureAwait(false);
                    return true;
                }

            case OrderStatuses.Releasing:
                {
                    var posted = await _ledger.FindPostingAsync(SettleKey(order.Id), cancellationToken)
                        .ConfigureAwait(false);

                    if (posted is { } settleId)
                    {
                        await FinishSettlementAsync(order, settleId, cancellationToken)
                            .ConfigureAwait(false);
                    }
                    else
                    {
                        // Nothing moved, so nothing is owed. Back to held, and the
                        // seller can scan again — rather than paying them out on
                        // the strength of a request that did not finish.
                        order.Status = OrderStatuses.Held;
                        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                    }

                    return true;
                }

            case OrderStatuses.Refunding:
                {
                    // A refund, unlike a release, is always safe to finish: the row
                    // records that somebody asked for it, and the money is the
                    // buyer's either way.
                    var postingId = await PostSettlementAsync(
                        order, order.RefundReason ?? OrderRefundReasons.Cancelled, cancellationToken)
                        .ConfigureAwait(false);
                    await FinishSettlementAsync(order, postingId, cancellationToken).ConfigureAwait(false);
                    return true;
                }

            default:
                return false;
        }
    }

    // ------------------------------------------------------------- internals

    /// <summary>
    /// Moves the listing to reserved and writes the pending order, in one
    /// transaction and in that order.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Listing first, order second — everywhere in this file. Two transactions
    /// that take the same two rows in opposite orders deadlock, and the one
    /// that would have done it here is a buyer arriving while a seller redeems.
    /// </para>
    /// <para>
    /// The <c>UPDATE … WHERE status = 'active'</c> is the race: two buyers
    /// reading an active listing at the same moment both try it, and exactly
    /// one updates a row. The loser sees zero rows and is told the item is
    /// gone, which it is.
    /// </para>
    /// </remarks>
    private async Task<Order> ClaimListingAsync(
        string buyerId, string buyerName, Guid listingId, CancellationToken cancellationToken)
    {
        await using var transaction = await _db.Database
            .BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        var now = _clock.GetUtcNow();
        var reserved = await _db.Listings.FromSql(
            $"""
             UPDATE marketplace.listings
             SET status = 'reserved', updated_at = {now}
             WHERE id = {listingId} AND status = 'active'
             RETURNING *
             """).ToListAsync(cancellationToken).ConfigureAwait(false);

        if (reserved.Count == 0)
        {
            // Tell "gone" from "never existed", because the client shows a
            // different screen and neither answer reveals anything: a listing
            // id is not a secret.
            var exists = await _db.Listings.AsNoTracking()
                .AnyAsync(l => l.Id == listingId, cancellationToken).ConfigureAwait(false);

            throw exists
                ? new MarketplaceException(
                    MarketplaceErrors.ListingUnavailable, 409,
                    "This listing is no longer on offer.")
                : new MarketplaceException(
                    MarketplaceErrors.ListingNotFound, 404, "No such listing.");
        }

        var listing = reserved[0];

        if (string.Equals(listing.SellerId, buyerId, StringComparison.Ordinal))
        {
            // Rolled back by the dispose below, so the listing stays active.
            throw new MarketplaceException(
                MarketplaceErrors.SelfPurchase, 422, "A seller cannot buy their own listing.");
        }

        var price = listing.Price;
        var order = new Order
        {
            Id = Guid.NewGuid(),
            ListingId = listing.Id,
            Listing = listing,
            BuyerId = buyerId,
            SellerId = listing.SellerId,
            BuyerName = buyerName,
            SellerName = listing.SellerName,
            ListingTitle = listing.Title,
            CurrencyCode = listing.CurrencyCode,
            AmountMinor = price.MinorUnits,
            FeeMinor = price.MultiplyByBasisPoints(_options.FeeBps, MidpointRounding.ToEven).MinorUnits,
            Code = RedemptionCode.New(),
            Status = OrderStatuses.Pending,
            CreatedAt = now,
            ExpiresAt = now + _options.HoldDuration,
        };

        _db.Orders.Add(order);

        try
        {
            await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateException e) when (IsUniqueViolation(e))
        {
            // orders_one_live_claim_per_listing. The listing said active and
            // the index disagreed, which means a concurrent buyer committed
            // between the two statements.
            throw new MarketplaceException(
                MarketplaceErrors.ListingUnavailable, 409,
                "This listing is no longer on offer.");
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return order;
    }

    private async Task MarkHeldAsync(Guid orderId, Guid postingId, CancellationToken cancellationToken)
    {
        await _db.Database.ExecuteSqlAsync(
            $"""
             UPDATE marketplace.orders
             SET status = 'held', hold_posting_id = {postingId}
             WHERE id = {orderId} AND status = 'pending'
             """, cancellationToken).ConfigureAwait(false);

        _db.ChangeTracker.Clear();
    }

    /// <summary>Undoes a reservation whose hold never posted.</summary>
    private async Task AbandonAsync(Order order, CancellationToken cancellationToken)
    {
        await using var transaction = await _db.Database
            .BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        await _db.Database.ExecuteSqlAsync(
            $"""
             UPDATE marketplace.listings
             SET status = 'active', updated_at = {_clock.GetUtcNow()}
             WHERE id = {order.ListingId} AND status = 'reserved'
             """, cancellationToken).ConfigureAwait(false);

        // Deleted rather than kept in a failed status: nothing happened, the
        // buyer saw an error, and a row here would hold the listing's one live
        // claim for ever.
        await _db.Database.ExecuteSqlAsync(
            $"DELETE FROM marketplace.orders WHERE id = {order.Id} AND status = 'pending'",
            cancellationToken).ConfigureAwait(false);

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        _db.ChangeTracker.Clear();
    }

    /// <summary>
    /// Frees the listing, but only once the ledger has confirmed that the hold
    /// never posted.
    /// </summary>
    /// <remarks>
    /// The distinction this draws is the whole reason <c>pending</c> exists as
    /// a status. A failure after <c>PostAsync</c> returns and a failure instead
    /// of it look identical from here, and they need opposite repairs.
    /// </remarks>
    private async Task AbandonIfNothingPostedAsync(Order order, CancellationToken cancellationToken)
    {
        try
        {
            var posted = await _ledger.FindPostingAsync(HoldKey(order.Id), cancellationToken)
                .ConfigureAwait(false);

            if (posted is null)
            {
                await AbandonAsync(order, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            // Could not find out. Leaving it pending is the safe answer: the
            // sweeper asks the same question again, later, when whatever broke
            // is more likely to be working.
        }
    }

    /// <summary>
    /// Frees the listing, and does not complain if it cannot.
    /// </summary>
    /// <remarks>
    /// Called while an exception is already on its way out. Failing to tidy up
    /// must not replace the error the caller actually needs to see, and the
    /// sweeper finds a pending order either way.
    /// </remarks>
    private async Task AbandonQuietlyAsync(Order order, CancellationToken cancellationToken)
    {
        try
        {
            await AbandonAsync(order, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception e) when (e is NpgsqlException or DbUpdateException or OperationCanceledException)
        {
        }
    }

    private async Task<Order> ClaimForReleaseAsync(
        string sellerId, string code, CancellationToken cancellationToken)
    {
        await using var transaction = await _db.Database
            .BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        var rows = await _db.Orders.FromSql(
            $"SELECT * FROM marketplace.orders WHERE code = {code} FOR UPDATE")
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        // Not "no such code" and not "not your sale": one answer, so that a
        // seller cannot use this endpoint to discover whether a code is real.
        if (rows.Count == 0 || !IsSeller(rows[0], sellerId)) throw CodeInvalid();

        var order = rows[0];
        switch (order.Status)
        {
            // Scanning twice is not an error. The seller gets the same answer.
            case OrderStatuses.Released:
                return order;

            // A previous attempt claimed it and did not finish. Carry on: the
            // ledger key makes the repeat post a no-op.
            case OrderStatuses.Releasing:
                break;

            case OrderStatuses.Held when order.ExpiresAt <= _clock.GetUtcNow():
                throw new MarketplaceException(
                    MarketplaceErrors.OrderExpired, 409,
                    "The hold ran out before this code was scanned.");

            case OrderStatuses.Held:
                order.Status = OrderStatuses.Releasing;
                await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                break;

            default:
                throw new MarketplaceException(
                    MarketplaceErrors.OrderNotHeld, 409,
                    "This order is no longer holding money.");
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return order;
    }

    private Task<Order> ClaimForRefundAsync(
        string actorId, Guid orderId, CancellationToken cancellationToken) =>
        ClaimForRefundAsync(actorId, orderId, OrderRefundReasons.Cancelled, cancellationToken);

    private async Task<Order> ClaimForRefundAsync(
        string actorId, Guid orderId, string reason, CancellationToken cancellationToken)
    {
        await using var transaction = await _db.Database
            .BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        var rows = await _db.Orders.FromSql(
            $"SELECT * FROM marketplace.orders WHERE id = {orderId} FOR UPDATE")
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        if (rows.Count == 0 || (!IsBuyer(rows[0], actorId) && !IsSeller(rows[0], actorId)))
        {
            throw new MarketplaceException(
                MarketplaceErrors.OrderNotFound, 404, "No such order.");
        }

        var order = rows[0];
        switch (order.Status)
        {
            case OrderStatuses.Cancelled:
            case OrderStatuses.Expired:
                return order;

            case OrderStatuses.Refunding:
                break;

            case OrderStatuses.Held:
                order.Status = OrderStatuses.Refunding;
                order.RefundReason = reason;
                await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                break;

            default:
                throw new MarketplaceException(
                    MarketplaceErrors.OrderNotHeld, 409,
                    order.Status is OrderStatuses.Released or OrderStatuses.Releasing
                        ? "The code has already been scanned; the sale is done."
                        : "This order is not holding money.");
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return order;
    }

    /// <summary>
    /// Moves the money out of escrow, to the seller or back to the buyer.
    /// </summary>
    /// <returns>The posting, whether this call wrote it or found it.</returns>
    private async Task<Guid> PostSettlementAsync(
        Order order, string? refundReason, CancellationToken cancellationToken)
    {
        var currency = order.Amount.Currency;
        var now = _clock.GetUtcNow();

        List<PostingLeg> legs = [new PostingLeg(AccountRef.Escrow(currency), -order.Amount)];
        PendingEvent announcement;

        if (refundReason is null)
        {
            legs.Add(new PostingLeg(AccountRef.User(order.SellerId, currency), order.SellerReceives));

            // A commission that rounds to nothing is not charged. The ledger
            // refuses a zero entry, so this must be an omitted leg rather than
            // a zero one — the same bug conversions had.
            if (order.Fee.IsPositive)
            {
                legs.Add(new PostingLeg(AccountRef.Fees(currency), order.Fee));
            }

            announcement = new PendingEvent(
                MarketplaceEvents.Context,
                MarketplaceEvents.OrderReleased,
                new OrderReleased(
                    order.Id, order.ListingId, order.BuyerId, order.SellerId,
                    order.Amount, order.Fee, now));
        }
        else
        {
            legs.Add(new PostingLeg(AccountRef.User(order.BuyerId, currency), order.Amount));

            announcement = new PendingEvent(
                MarketplaceEvents.Context,
                MarketplaceEvents.OrderRefunded,
                new OrderRefunded(
                    order.Id, order.ListingId, order.BuyerId, order.SellerId,
                    order.Amount, refundReason, now));
        }

        var receipt = await _ledger.PostAsync(
            new PostingRequest(
                Kind: "payment",
                Legs: legs,
                IdempotencyKey: SettleKey(order.Id),
                Metadata: new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["orderId"] = order.Id.ToString("D", CultureInfo.InvariantCulture),
                    ["listingId"] = order.ListingId.ToString("D", CultureInfo.InvariantCulture),
                    ["listingTitle"] = order.ListingTitle,
                    ["outcome"] = refundReason ?? "released",
                    ["counterpartyName"] = refundReason is null ? order.BuyerName : order.SellerName,
                },
                Events: [announcement]),
            cancellationToken).ConfigureAwait(false);

        return receipt.PostingId;
    }

    /// <summary>
    /// Records what the settlement posting did, and moves the listing with it.
    /// </summary>
    private async Task FinishSettlementAsync(
        Order order, Guid postingId, CancellationToken cancellationToken)
    {
        var released = order.Status is OrderStatuses.Releasing;
        var outcome = released
            ? OrderStatuses.Released
            : order.RefundReason == OrderRefundReasons.Expired
                ? OrderStatuses.Expired
                : OrderStatuses.Cancelled;

        var now = _clock.GetUtcNow();

        await using var transaction = await _db.Database
            .BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        // Listing before order, as everywhere else here. A sold item stays
        // sold; a refunded one goes back on offer. Two statements rather than
        // one with a conditional: an interpolated SQL string has to be built
        // where it is written, or it stops being a parameterised query.
        if (released)
        {
            await _db.Database.ExecuteSqlAsync(
                $"""
                 UPDATE marketplace.listings SET status = 'sold', updated_at = {now}
                 WHERE id = {order.ListingId} AND status = 'reserved'
                 """, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            await _db.Database.ExecuteSqlAsync(
                $"""
                 UPDATE marketplace.listings SET status = 'active', updated_at = {now}
                 WHERE id = {order.ListingId} AND status = 'reserved'
                 """, cancellationToken).ConfigureAwait(false);
        }

        await _db.Database.ExecuteSqlAsync(
            $"""
             UPDATE marketplace.orders
             SET status = {outcome}, settle_posting_id = {postingId}, settled_at = {now}
             WHERE id = {order.Id} AND status IN ('releasing', 'refunding')
             """, cancellationToken).ConfigureAwait(false);

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

        _db.ChangeTracker.Clear();
        order.Status = outcome;
        order.SettlePostingId = postingId;
        order.SettledAt = now;
    }

    private static MarketplaceException CodeInvalid() =>
        new(MarketplaceErrors.CodeInvalid, 422, "That code does not open a sale of yours.");

    private static bool IsBuyer(Order order, string userId) =>
        string.Equals(order.BuyerId, userId, StringComparison.Ordinal);

    private static bool IsSeller(Order order, string userId) =>
        string.Equals(order.SellerId, userId, StringComparison.Ordinal);

    private static bool IsUniqueViolation(DbUpdateException e) =>
        e.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation };

    private static ListingDto Project(Listing listing) => new(
        Id: listing.Id.ToString("D", CultureInfo.InvariantCulture),
        SellerId: listing.SellerId,
        SellerName: listing.SellerName,
        Title: listing.Title,
        Description: listing.Description,
        Category: listing.Category,
        Condition: listing.Condition,
        Price: listing.Price,
        Location: listing.Location,
        Photos: [.. listing.Photos],
        Status: listing.Status,
        PublishedAt: listing.PublishedAt);

    /// <param name="forBuyer">
    /// Whether the code travels. Only the buyer may see it; see
    /// <see cref="OrderDto.Code"/>.
    /// </param>
    private static OrderDto Project(Order order, bool forBuyer) => new(
        Id: order.Id.ToString("D", CultureInfo.InvariantCulture),
        ListingId: order.ListingId.ToString("D", CultureInfo.InvariantCulture),
        ListingTitle: order.ListingTitle,
        BuyerId: order.BuyerId,
        BuyerName: order.BuyerName,
        SellerId: order.SellerId,
        SellerName: order.SellerName,
        Amount: order.Amount,
        Fee: order.Fee,
        SellerReceives: order.SellerReceives,
        Status: order.Status,
        CreatedAt: order.CreatedAt,
        ExpiresAt: order.ExpiresAt,
        // Withheld once the order is over as well: there is nothing left for
        // it to open, and a code on screen invites somebody to try.
        Code: forBuyer && !OrderStatuses.Terminal.Contains(order.Status) ? order.Code : null);

    /// <summary>
    /// One page, newest first, with the cursor built from <c>seq</c>.
    /// </summary>
    /// <remarks>
    /// One row more than asked for is fetched, so the presence of a next page
    /// is known rather than guessed — a full page can be the last one.
    /// </remarks>
    private static async Task<CursorPage<TDto>> PageAsync<TRow, TDto>(
        IQueryable<TRow> query,
        int limit,
        string? cursor,
        Func<TRow, TDto> project,
        CancellationToken cancellationToken)
        where TRow : class
    {
        var take = Math.Clamp(limit, 1, 100) + 1;
        var after = DecodeCursor(cursor);

        var rows = await query
            .Where(r => after == null || EF.Property<long>(r, nameof(Listing.Seq)) < after)
            .OrderByDescending(r => EF.Property<long>(r, nameof(Listing.Seq)))
            .Take(take)
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        string? next = null;
        if (rows.Count == take)
        {
            rows.RemoveAt(rows.Count - 1);
            next = EncodeCursor(SeqOf(rows[^1]));
        }

        return new CursorPage<TDto>([.. rows.Select(project)], next);
    }

    private static long SeqOf<TRow>(TRow row) => row switch
    {
        Listing listing => listing.Seq,
        Order order => order.Seq,
        _ => throw new InvalidOperationException($"{typeof(TRow).Name} is not pageable."),
    };

    /// <summary>
    /// Opaque on purpose: a client that reads a cursor acquires a dependency
    /// on the ordering, and the ordering is ours to change.
    /// </summary>
    private static string EncodeCursor(long seq) =>
        Convert.ToBase64String(Encoding.UTF8.GetBytes(seq.ToString(CultureInfo.InvariantCulture)));

    private static long? DecodeCursor(string? cursor)
    {
        if (string.IsNullOrWhiteSpace(cursor)) return null;

        try
        {
            var text = Encoding.UTF8.GetString(Convert.FromBase64String(cursor));
            return long.TryParse(text, CultureInfo.InvariantCulture, out var seq) ? seq : null;
        }
        catch (FormatException)
        {
            // A cursor we did not issue. The first page is a better answer than
            // a 500 for what is almost always a stale link.
            return null;
        }
    }
}

/// <summary>Why an order's money went back to the buyer.</summary>
public static class OrderRefundReasons
{
    public const string Cancelled = "cancelled";
    public const string Expired = "expired";
}
