using System.Globalization;
using System.Text;
using IslaPay.Catalog.Contracts;
using IslaPay.Identity.Contracts;
using IslaPay.Ledger.Contracts;
using IslaPay.P2P.Contracts;
using IslaPay.Platform;
using IslaPay.Platform.Api;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace IslaPay.P2P;

/// <summary>
/// Instant exchange against IslaPay, and the wait that the word "instant"
/// hides.
/// </summary>
/// <remarks>
/// <para>
/// The price is instant. The settlement is not: one leg of every trade happens
/// outside IslaPay, through a Cuban bank, sent or received by a person. So a
/// sell takes the user's money now and owes them local currency until an
/// operator confirms it was sent, and a buy posts nothing at all until an
/// operator confirms the local money arrived. Money is never credited before
/// it exists.
/// </para>
/// <para>
/// The movement machinery is the marketplace's, for the same reason: the
/// ledger owns its transaction, so moving money and recording that it moved
/// are two commits. Claim the row into <c>settling</c>, post, record. A
/// process that dies in between leaves the claim for <see cref="RepairAsync"/>
/// to resolve against the ledger rather than against a guess.
/// </para>
/// <para>
/// One idempotency key, <c>p2p:settle:{tradeId}</c>, covers all three ways a
/// trade can end — paid out, refunded, credited. They are mutually exclusive
/// and they compete for the same unique index in the ledger, so the settlement
/// fund moves at most once per trade even if everything written here is wrong.
/// </para>
/// </remarks>
public sealed class P2PService
{
    private readonly P2PDbContext _db;
    private readonly ILedger _ledger;
    private readonly ICurrencyCatalog _catalog;
    private readonly IUserDirectory _directory;
    private readonly P2POptions _options;
    private readonly TimeProvider _clock;

    public P2PService(
        P2PDbContext db,
        ILedger ledger,
        ICurrencyCatalog catalog,
        IUserDirectory directory,
        P2POptions options,
        TimeProvider? clock = null)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(ledger);
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(directory);
        ArgumentNullException.ThrowIfNull(options);
        _db = db;
        _ledger = ledger;
        _catalog = catalog;
        _directory = directory;
        _options = options;
        _clock = clock ?? TimeProvider.System;
    }

    /// <summary>The ledger key for a sell's commit: wallet out, local owed.</summary>
    public static string CommitKey(Guid tradeId) =>
        $"p2p:commit:{tradeId.ToString("D", CultureInfo.InvariantCulture)}";

    /// <summary>
    /// The ledger key for however a trade ends.
    /// </summary>
    /// <remarks>
    /// One key for a payout, a refund and a credit. See the class remarks:
    /// they cannot all happen, and sharing the key makes that a guarantee in
    /// the ledger rather than a hope in this file.
    /// </remarks>
    public static string SettleKey(Guid tradeId) =>
        $"p2p:settle:{tradeId.ToString("D", CultureInfo.InvariantCulture)}";

    // ---------------------------------------------------------------- reading

    /// <summary>The rails, with both sides of each spread per wallet currency.</summary>
    public async Task<IReadOnlyList<P2PMethodDto>> MethodsAsync(
        CancellationToken cancellationToken = default)
    {
        var methods = await _db.Methods.AsNoTracking()
            .OrderBy(m => m.Name)
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        var rates = await RatesByMethodAsync(cancellationToken).ConfigureAwait(false);

        return
        [
            .. methods.Select(method =>
            {
                var offered = rates.GetValueOrDefault(method.Id) ?? [];
                return new P2PMethodDto(
                    Id: method.Id,
                    Name: method.Name,
                    Code: method.LocalCurrencyCode,
                    // A rail with no published price cannot be traded however
                    // its switch is set. Saying so here keeps the client from
                    // offering a price that does not exist.
                    Available: method.Available && offered.Count > 0,
                    Minimum: method.Minimum,
                    Maximum: method.Maximum,
                    Rates: offered);
            }),
        ];
    }

    /// <summary>The rails as the desk edits them, switched off ones included.</summary>
    public async Task<IReadOnlyList<P2PAdminMethodDto>> AdminMethodsAsync(
        CancellationToken cancellationToken = default)
    {
        var methods = await _db.Methods.AsNoTracking()
            .OrderBy(m => m.Name)
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        var rates = await RatesByMethodAsync(cancellationToken).ConfigureAwait(false);

        return [.. methods.Select(m => AdminView(m, rates.GetValueOrDefault(m.Id) ?? []))];
    }

    /// <summary>What a trade would look like if it were placed now.</summary>
    /// <remarks>
    /// Commits to nothing and stores nothing. A reason with
    /// <c>executable: false</c> is a successful answer describing a trade that
    /// cannot happen, not a failure — see <see cref="P2PQuoteDto"/>.
    /// </remarks>
    public async Task<P2PQuoteDto> QuoteAsync(
        P2PQuoteRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var method = await RequireMethodAsync(request.MethodId, cancellationToken)
            .ConfigureAwait(false);

        RequireTradeableAmount(request.Amount);

        var rate = await CurrentRateAsync(
                method, request.Side, request.Amount.Currency.Code, cancellationToken)
            .ConfigureAwait(false);

        if (rate is null)
        {
            return Unexecutable(method, request, P2PErrors.RateUnavailable);
        }

        var priced = Price(method, request.Side, request.Amount, rate.Value);

        RequireWithinLimits(method, priced.Local);

        if (!method.Available)
        {
            return priced with { Executable = false, Reason = P2PErrors.MethodUnavailable };
        }

        var shortfall = await FundShortfallAsync(method, request.Side, priced, cancellationToken)
            .ConfigureAwait(false);

        return shortfall is null
            ? priced
            : priced with { Executable = false, Reason = P2PErrors.FundUnavailable };
    }

    /// <summary>One trade, for the user it belongs to.</summary>
    public async Task<P2PTradeDto> TradeAsync(
        string userId, Guid id, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);

        var trade = await _db.Trades.AsNoTracking()
            .FirstOrDefaultAsync(t => t.Id == id, cancellationToken).ConfigureAwait(false);

        // One answer for "no such trade" and "not yours", so an id cannot be
        // confirmed by asking.
        if (trade is null || !string.Equals(trade.UserId, userId, StringComparison.Ordinal))
        {
            throw new P2PException(P2PErrors.TradeNotFound, 404, "No such trade.");
        }

        return await ProjectAsync(trade, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The caller's trades, newest first.</summary>
    public async Task<CursorPage<P2PTradeDto>> TradesOfAsync(
        string userId, int limit, string? cursor, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);

        var take = Math.Clamp(limit, 1, 100) + 1;
        var after = DecodeCursor(cursor);

        var rows = await _db.Trades.AsNoTracking()
            .Where(t => t.UserId == userId && (after == null || t.Seq < after))
            .OrderByDescending(t => t.Seq)
            .Take(take)
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        string? next = null;
        if (rows.Count == take)
        {
            rows.RemoveAt(rows.Count - 1);
            next = EncodeCursor(rows[^1].Seq);
        }

        var items = new List<P2PTradeDto>(rows.Count);
        foreach (var row in rows)
        {
            items.Add(await ProjectAsync(row, cancellationToken).ConfigureAwait(false));
        }

        return new CursorPage<P2PTradeDto>(items, next);
    }

    // ---------------------------------------------------------------- trading

    /// <summary>Places a trade.</summary>
    /// <remarks>
    /// The two sides do genuinely different things, and the asymmetry is the
    /// point: a sell moves money before anybody has confirmed anything,
    /// because the user's own balance is what backs it. A buy moves nothing,
    /// because what backs it is a transfer that has not happened yet.
    /// </remarks>
    public async Task<P2PTradeDto> OpenAsync(
        string userId, P2PTradeRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);
        ArgumentNullException.ThrowIfNull(request);

        var user = await _directory.FindByIdAsync(userId, cancellationToken).ConfigureAwait(false)
            ?? throw new P2PException(
                P2PErrors.TradeNotFound, 422, "The account no longer exists.");

        if (_options.RequireVerifiedPhone && !user.PhoneVerified)
        {
            throw new P2PException(
                P2PErrors.PhoneNotVerified, 403,
                "The account must prove its phone number before money can move.");
        }

        var method = await RequireMethodAsync(request.MethodId, cancellationToken)
            .ConfigureAwait(false);

        if (!method.Available)
        {
            throw new P2PException(
                P2PErrors.MethodUnavailable, 409, "That rail is not trading right now.");
        }

        RequireTradeableAmount(request.Amount);

        var rate = await CurrentRateAsync(
                method, request.Side, request.Amount.Currency.Code, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new P2PException(
                P2PErrors.RateUnavailable, 409,
                $"That rail has no published {Wire(request.Side)} rate for {request.Amount.Currency.Code}.");

        RequireQuoteStillHolds(request.QuotedRate, rate);

        var priced = Price(method, request.Side, request.Amount, rate);

        if (!priced.Local.IsPositive)
        {
            throw new P2PException(
                P2PErrors.InvalidAmount, 422,
                "That amount converts to nothing at the current rate.");
        }

        RequireWithinLimits(method, priced.Local);

        if (await FundShortfallAsync(method, request.Side, priced, cancellationToken)
            .ConfigureAwait(false) is { } short_)
        {
            var failure = new P2PException(
                P2PErrors.FundUnavailable, 422,
                "IslaPay cannot cover that trade right now.");
            failure.Facts["currency"] = short_.Currency.Code;
            failure.Facts["shortfall"] = short_.ToString();
            throw failure;
        }

        var payoutTo = request.Side == P2PSide.Sell ? RequirePayoutDestination(request.PayoutTo) : null;

        var trade = await InsertAsync(user, method, request.Side, priced, rate, payoutTo, cancellationToken)
            .ConfigureAwait(false);

        // A buy stops here. Nothing has moved and nothing should: the user now
        // owes local money and the ledger will hear about it when it arrives.
        if (request.Side == P2PSide.Buy) return await ProjectAsync(trade, cancellationToken).ConfigureAwait(false);

        await CommitSellAsync(trade, cancellationToken).ConfigureAwait(false);
        return await ProjectAsync(trade, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The user calls the trade off.</summary>
    /// <remarks>
    /// A buy simply stops being expected — nothing moved. A sell has already
    /// taken their money, so calling it off is a refund, and it races the
    /// operator paying it out: the row lock decides, and whichever gets there
    /// first is the one that happens.
    /// </remarks>
    public async Task<P2PTradeDto> CancelAsync(
        string userId, Guid id, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);

        var trade = await LockAsync(id, cancellationToken).ConfigureAwait(false)
            ?? throw new P2PException(P2PErrors.TradeNotFound, 404, "No such trade.");

        if (!string.Equals(trade.UserId, userId, StringComparison.Ordinal))
        {
            throw new P2PException(P2PErrors.TradeNotFound, 404, "No such trade.");
        }

        if (trade.Status == P2PTradeStatuses.AwaitingPayment)
        {
            await SetStatusAsync(trade, P2PTradeStatuses.Expired, cancellationToken)
                .ConfigureAwait(false);
            return await ProjectAsync(trade, cancellationToken).ConfigureAwait(false);
        }

        if (trade.Status is P2PTradeStatuses.Expired or P2PTradeStatuses.Refunded)
        {
            return await ProjectAsync(trade, cancellationToken).ConfigureAwait(false);
        }

        if (trade.Status != P2PTradeStatuses.AwaitingPayout)
        {
            throw new P2PException(
                P2PErrors.TradeNotOpen, 409, "This trade is no longer waiting on anything.");
        }

        // Stored as the failure reason and shown to the user as written, like
        // an operator's; so in their language, not a log line's.
        await RefundAsync(trade, "Cancelaste la venta antes de que se enviaran los pesos.", operatorId: null, cancellationToken)
            .ConfigureAwait(false);

        return await ProjectAsync(trade, cancellationToken).ConfigureAwait(false);
    }

    // --------------------------------------------------------------- operator

    /// <summary>What is waiting on a person, oldest first.</summary>
    public async Task<IReadOnlyList<P2PQueueItemDto>> QueueAsync(
        int limit, CancellationToken cancellationToken = default)
    {
        var now = _clock.GetUtcNow();

        var rows = await _db.Trades.AsNoTracking()
            .Where(t => t.Status == P2PTradeStatuses.AwaitingPayout
                     || t.Status == P2PTradeStatuses.AwaitingPayment)
            // Oldest first: the person who has waited longest is served next.
            .OrderBy(t => t.CreatedAt)
            .Take(Math.Clamp(limit, 1, 200))
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        return [.. rows.Select(t => OperatorView(t, now))];
    }

    /// <summary>The operator sent a seller their local money.</summary>
    public async Task<P2PTradeDto> ConfirmPayoutAsync(
        string operatorId, Guid id, string reference, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operatorId);

        var trade = await ClaimForSettlementAsync(
            id, P2PSide.Sell, P2PSettleIntents.Payout,
            [P2PTradeStatuses.AwaitingPayout],
            operatorId, RequireReference(reference), failureReason: null, cancellationToken)
            .ConfigureAwait(false);

        if (trade.Status == P2PTradeStatuses.Completed)
        {
            return await ProjectAsync(trade, cancellationToken).ConfigureAwait(false);
        }

        await SettleAsync(trade, cancellationToken).ConfigureAwait(false);
        return await ProjectAsync(trade, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The operator could not pay a seller. The money goes back.</summary>
    public async Task<P2PTradeDto> FailPayoutAsync(
        string operatorId, Guid id, string reason, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operatorId);

        var trade = await LockAsync(id, cancellationToken).ConfigureAwait(false)
            ?? throw new P2PException(P2PErrors.TradeNotFound, 404, "No such trade.");

        if (ParseSide(trade.Side) != P2PSide.Sell)
        {
            throw new P2PException(
                P2PErrors.WrongSide, 409, "Only a sell can fail to pay out.");
        }

        if (trade.Status is P2PTradeStatuses.Refunded)
        {
            return await ProjectAsync(trade, cancellationToken).ConfigureAwait(false);
        }

        if (trade.Status != P2PTradeStatuses.AwaitingPayout
            && !(trade.Status == P2PTradeStatuses.Settling
                 && trade.SettleIntent == P2PSettleIntents.Refund))
        {
            throw new P2PException(
                P2PErrors.TradeNotOpen, 409, "This trade is not waiting on a payout.");
        }

        await RefundAsync(trade, RequireReason(reason), operatorId, cancellationToken)
            .ConfigureAwait(false);

        return await ProjectAsync(trade, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The operator confirms a buyer's local money arrived.</summary>
    /// <remarks>
    /// Accepted from <c>expired</c> as well as from <c>awaiting_payment</c>,
    /// deliberately. Expiry means IslaPay has stopped expecting the money, not
    /// that it will refuse it: a buyer who transfers at the last minute and an
    /// operator who looks a few minutes later would otherwise leave IslaPay
    /// holding money it has no way to account for. The rate is frozen on the
    /// trade, so honouring it late is still honouring the agreed price.
    /// </remarks>
    public async Task<P2PTradeDto> ConfirmReceiptAsync(
        string operatorId, Guid id, string reference, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operatorId);

        var trade = await ClaimForSettlementAsync(
            id, P2PSide.Buy, P2PSettleIntents.Credit,
            [P2PTradeStatuses.AwaitingPayment, P2PTradeStatuses.Expired],
            operatorId, RequireReference(reference), failureReason: null, cancellationToken)
            .ConfigureAwait(false);

        if (trade.Status == P2PTradeStatuses.Completed)
        {
            return await ProjectAsync(trade, cancellationToken).ConfigureAwait(false);
        }

        await SettleAsync(trade, cancellationToken).ConfigureAwait(false);
        return await ProjectAsync(trade, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Publishes a rate. Appends; never edits.</summary>
    public async Task SetRateAsync(
        string operatorId, P2PRateUpdate update, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operatorId);
        ArgumentNullException.ThrowIfNull(update);

        var method = await RequireMethodAsync(update.MethodId, cancellationToken)
            .ConfigureAwait(false);

        // Empty withdraws the side. Anything else has to be a price.
        decimal? rate = null;
        if (!string.IsNullOrWhiteSpace(update.Rate))
        {
            if (!decimal.TryParse(
                    update.Rate, NumberStyles.Number, CultureInfo.InvariantCulture, out var parsed)
                || parsed <= 0)
            {
                throw new P2PException(
                    P2PErrors.RateUnavailable, 422, "A rate must be a positive decimal.");
            }

            rate = parsed;
        }

        // The operator names a currency and the catalogue says what it is.
        // Taking the scale from the request would let a typo quote a rate
        // against a unit a thousand times larger than the one it settles in.
        Currency wallet;
        try
        {
            wallet = _catalog.Require(update.WalletCurrency);
        }
        catch (UnknownCurrencyException e)
        {
            throw new P2PException(P2PErrors.InvalidCurrency, 422, e.Message);
        }

        if (!IsHoldable(wallet.Code))
        {
            throw new P2PException(
                P2PErrors.InvalidCurrency, 422,
                $"{wallet.Code} is not held in wallets, so it cannot be priced against {method.LocalCurrencyCode}.");
        }

        _db.Rates.Add(new TradeRate
        {
            MethodId = method.Id,
            Side = Wire(update.Side),
            WalletCurrencyCode = wallet.Code,
            WalletScale = wallet.Scale,
            Rate = rate,
            EffectiveFrom = _clock.GetUtcNow(),
            SetBy = operatorId,
        });

        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        _db.ChangeTracker.Clear();
    }

    /// <summary>The longest name a rail may carry; it sits on one line of a phone.</summary>
    public const int MaximumMethodNameLength = 40;

    /// <summary>Opens a rail for a local currency, switched off and unpriced.</summary>
    /// <remarks>
    /// Starting off is the point: a rail that went live on creation would
    /// quote the moment the form was submitted, before anybody had set a
    /// price or said where buyers pay.
    /// </remarks>
    public async Task<P2PAdminMethodDto> CreateMethodAsync(
        P2PMethodCreate create, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(create);

        var local = RequireLocalCurrency(create.Currency);
        var (minimum, maximum) = RequireLimits(local, create.Minimum, create.Maximum);
        var name = RequireMethodName(create.Name ?? local.Code);
        var instructions = RequireInstructions(create.Instructions ?? string.Empty);
        var id = local.Code.ToLowerInvariant();

        if (await _db.Methods.AnyAsync(
                m => m.Id == id || m.LocalCurrencyCode == local.Code, cancellationToken)
            .ConfigureAwait(false))
        {
            throw new P2PException(
                P2PErrors.MethodExists, 409, $"There is already a rail for {local.Code}.");
        }

        var method = new TradeMethod
        {
            Id = id,
            Name = name,
            LocalCurrencyCode = local.Code,
            LocalScale = local.Scale,
            Available = false,
            Instructions = instructions,
            MinimumMinor = minimum.MinorUnits,
            MaximumMinor = maximum.MinorUnits,
            UpdatedAt = _clock.GetUtcNow(),
        };
        _db.Methods.Add(method);

        try
        {
            await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateException e) when (IsUniqueViolation(e))
        {
            // Two operators creating the same rail at once: the second is told
            // what the first already did.
            throw new P2PException(
                P2PErrors.MethodExists, 409, $"There is already a rail for {local.Code}.");
        }
        finally
        {
            _db.ChangeTracker.Clear();
        }

        return AdminView(method, []);
    }

    /// <summary>Renames a rail or moves its limits. Leaves out what is not sent.</summary>
    public async Task<P2PAdminMethodDto> UpdateMethodAsync(
        string methodId, P2PMethodUpdate update, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(update);

        var method = await _db.Methods
            .FirstOrDefaultAsync(m => m.Id == methodId, cancellationToken).ConfigureAwait(false)
            ?? throw new P2PException(P2PErrors.MethodNotFound, 404, "No such rail.");

        if (update.Name is not null)
        {
            method.Name = RequireMethodName(update.Name);
        }

        if (update.Minimum is not null || update.Maximum is not null)
        {
            var (minimum, maximum) = RequireLimits(
                method.LocalCurrency,
                update.Minimum ?? method.Minimum,
                update.Maximum ?? method.Maximum);
            method.MinimumMinor = minimum.MinorUnits;
            method.MaximumMinor = maximum.MinorUnits;
        }

        method.UpdatedAt = _clock.GetUtcNow();
        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        _db.ChangeTracker.Clear();

        var rates = await RatesByMethodAsync(cancellationToken).ConfigureAwait(false);
        return AdminView(method, rates.GetValueOrDefault(method.Id) ?? []);
    }

    /// <summary>
    /// Finds trades by reference or status, newest first.
    /// </summary>
    /// <remarks>
    /// The queue only shows what is waiting, so a buy that expired and was
    /// then paid late — which <see cref="ConfirmReceiptAsync"/> still accepts
    /// — has nowhere to be found without this. The reference is what a buyer
    /// writes on the transfer, and what the operator has in hand.
    /// </remarks>
    public async Task<IReadOnlyList<P2PQueueItemDto>> SearchTradesAsync(
        string? reference, string? status, int limit, CancellationToken cancellationToken = default)
    {
        var query = _db.Trades.AsNoTracking();

        if (!string.IsNullOrWhiteSpace(reference))
        {
            // As the operator has it: from a bank note, maybe without the
            // dash and in whatever case the banking app printed it.
            var wanted = new string([.. reference.Where(char.IsLetterOrDigit)]).ToUpperInvariant();
            if (wanted.Length == 8) wanted = $"{wanted[..4]}-{wanted[4..]}";
            query = query.Where(t => t.Reference == wanted);
        }

        if (!string.IsNullOrWhiteSpace(status))
        {
            var wanted = status.Trim();
            if (!P2PTradeStatuses.All.Contains(wanted))
            {
                throw new P2PException(
                    P2PErrors.InvalidAmount, 422, $"'{wanted}' is not a trade status.");
            }

            query = query.Where(t => t.Status == wanted);
        }

        var rows = await query
            .OrderByDescending(t => t.Seq)
            .Take(Math.Clamp(limit, 1, 200))
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        var now = _clock.GetUtcNow();
        return [.. rows.Select(t => OperatorView(t, now))];
    }

    /// <summary>Switches a rail on or off.</summary>
    public async Task SetAvailabilityAsync(
        string methodId, bool available, CancellationToken cancellationToken = default)
    {
        var method = await _db.Methods
            .FirstOrDefaultAsync(m => m.Id == methodId, cancellationToken).ConfigureAwait(false)
            ?? throw new P2PException(P2PErrors.MethodNotFound, 404, "No such rail.");

        method.Available = available;
        method.UpdatedAt = _clock.GetUtcNow();
        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        _db.ChangeTracker.Clear();
    }

    /// <summary>The longest instructions a rail may carry.</summary>
    public const int MaximumInstructionsLength = 1000;

    /// <summary>
    /// Sets what a buyer is told about where to send the local money.
    /// </summary>
    /// <remarks>
    /// Read at the moment a buy is shown, not copied onto the trade: if the
    /// desk changes account, a buyer who has not paid yet should see the new
    /// one rather than the one that was current when they pressed the button.
    /// </remarks>
    public async Task SetInstructionsAsync(
        string methodId, string instructions, CancellationToken cancellationToken = default)
    {
        var text = RequireInstructions(instructions);

        var method = await _db.Methods
            .FirstOrDefaultAsync(m => m.Id == methodId, cancellationToken).ConfigureAwait(false)
            ?? throw new P2PException(P2PErrors.MethodNotFound, 404, "No such rail.");

        method.Instructions = text;
        method.UpdatedAt = _clock.GetUtcNow();
        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        _db.ChangeTracker.Clear();
    }

    // -------------------------------------------------------------- the repair

    /// <summary>
    /// Finishes movements a crash interrupted, and stops expecting buys that
    /// ran out of time.
    /// </summary>
    /// <remarks>
    /// Nothing here decides that a committed sell should be refunded. A sell
    /// has taken somebody's money and the only honest endings are paying them
    /// or giving it back, and neither is a decision a timer is entitled to
    /// make. It waits for a person, however long that takes.
    /// </remarks>
    /// <returns>How many trades it touched.</returns>
    public async Task<int> RepairAsync(CancellationToken cancellationToken = default)
    {
        var now = _clock.GetUtcNow();
        var staleBefore = now - _options.InFlightGrace;
        var touched = 0;

        var candidates = await _db.Trades.AsNoTracking()
            .Where(t =>
                ((t.Status == P2PTradeStatuses.Pending || t.Status == P2PTradeStatuses.Settling)
                    && t.CreatedAt <= staleBefore)
                || (t.Status == P2PTradeStatuses.AwaitingPayment && t.ExpiresAt <= now))
            .OrderBy(t => t.Seq)
            .Take(100)
            .Select(t => t.Id)
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        foreach (var id in candidates)
        {
            if (await RepairOneAsync(id, cancellationToken).ConfigureAwait(false)) touched++;
        }

        return touched;
    }

    private async Task<bool> RepairOneAsync(Guid id, CancellationToken cancellationToken)
    {
        var trade = await LockAsync(id, cancellationToken).ConfigureAwait(false);
        if (trade is null) return false;

        switch (trade.Status)
        {
            case P2PTradeStatuses.Pending:
                {
                    var posted = await _ledger.FindPostingAsync(CommitKey(trade.Id), cancellationToken)
                        .ConfigureAwait(false);

                    if (posted is { } commitId)
                    {
                        // The commit landed and the request died before saying so.
                        trade.CommitPostingId = commitId;
                        await SetStatusAsync(trade, P2PTradeStatuses.AwaitingPayout, cancellationToken)
                            .ConfigureAwait(false);
                    }
                    else
                    {
                        await DiscardAsync(trade, cancellationToken).ConfigureAwait(false);
                    }

                    return true;
                }

            case P2PTradeStatuses.Settling:
                {
                    var posted = await _ledger.FindPostingAsync(SettleKey(trade.Id), cancellationToken)
                        .ConfigureAwait(false);

                    if (posted is { } settleId)
                    {
                        await FinishSettlementAsync(trade, settleId, cancellationToken)
                            .ConfigureAwait(false);
                    }
                    else
                    {
                        // Nothing moved, so nothing is owed on it. Back to where it
                        // was, and a person decides again — rather than paying out
                        // on the strength of a request that did not finish.
                        await SetStatusAsync(
                            trade,
                            trade.SettleIntent == P2PSettleIntents.Credit
                                ? P2PTradeStatuses.AwaitingPayment
                                : P2PTradeStatuses.AwaitingPayout,
                            cancellationToken,
                            clearIntent: true).ConfigureAwait(false);
                    }

                    return true;
                }

            case P2PTradeStatuses.AwaitingPayment when trade.ExpiresAt <= _clock.GetUtcNow():
                // Stops being expected. Nothing was ever posted, so there is
                // nothing to reverse — and an operator may still honour it.
                await SetStatusAsync(trade, P2PTradeStatuses.Expired, cancellationToken)
                    .ConfigureAwait(false);
                return true;

            default:
                return false;
        }
    }

    // -------------------------------------------------------------- internals

    /// <summary>
    /// Writes the trade row before any money moves.
    /// </summary>
    /// <remarks>
    /// A sell starts <c>pending</c>: the row exists, the posting has not
    /// happened, and if this process dies now the sweeper finds a row with no
    /// posting and throws it away. The opposite order would leave money in
    /// escrow with nothing pointing at it.
    /// </remarks>
    /// <summary>The longest payout destination a sell may carry.</summary>
    public const int MaximumPayoutDestinationLength = 64;

    /// <summary>
    /// Where a seller's pesos go, or a refusal before any money moves.
    /// </summary>
    /// <remarks>
    /// Checked last among the refusals, after the price and the fund: the
    /// cheaper answers — wrong amount, no rate — are the ones a person fixes
    /// first, and they do not depend on this.
    /// </remarks>
    private static string RequirePayoutDestination(string? payoutTo)
    {
        var text = payoutTo?.Trim();
        if (string.IsNullOrEmpty(text))
        {
            throw new P2PException(
                P2PErrors.PayoutDestinationRequired, 422,
                "A sale has to say where to send the local money.");
        }

        if (text.Length > MaximumPayoutDestinationLength)
        {
            throw new P2PException(
                P2PErrors.InvalidPayoutDestination, 422,
                $"A payout destination is at most {MaximumPayoutDestinationLength} characters.");
        }

        return text;
    }

    private async Task<Trade> InsertAsync(
        DirectoryUser user,
        TradeMethod method,
        P2PSide side,
        P2PQuoteDto priced,
        decimal rate,
        string? payoutTo,
        CancellationToken cancellationToken)
    {
        var now = _clock.GetUtcNow();

        var trade = new Trade
        {
            Id = Guid.NewGuid(),
            UserId = user.UserId,
            UserName = user.Name,
            Side = Wire(side),
            MethodId = method.Id,
            MethodName = method.Name,
            WalletCurrencyCode = priced.Amount.Currency.Code,
            WalletScale = priced.Amount.Currency.Scale,
            AmountMinor = priced.Amount.MinorUnits,
            FeeMinor = priced.Fee.MinorUnits,
            LocalCurrencyCode = priced.Local.Currency.Code,
            LocalScale = priced.Local.Currency.Scale,
            LocalMinor = priced.Local.MinorUnits,
            Rate = rate,
            Reference = TradeReference.New(),
            PayoutTo = payoutTo,
            Status = side == P2PSide.Sell
                ? P2PTradeStatuses.Pending
                : P2PTradeStatuses.AwaitingPayment,
            CreatedAt = now,
            ExpiresAt = now + (side == P2PSide.Sell
                ? _options.PayoutTarget
                : _options.PaymentWindow),
        };

        _db.Trades.Add(trade);

        try
        {
            await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateException e) when (IsUniqueViolation(e))
        {
            // The reference collided. Forty bits makes that rare enough that
            // one retry is the whole recovery.
            _db.ChangeTracker.Clear();
            trade.Reference = TradeReference.New();
            _db.Trades.Add(trade);
            await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }

        return trade;
    }

    /// <summary>
    /// A sell's commit: the wallet leg and the local obligation, in one
    /// posting.
    /// </summary>
    /// <remarks>
    /// Five legs across two currencies, and each currency balances on its own,
    /// which is what the ledger requires. The wallet side takes the fee and
    /// hands the rest to the fund; the local side moves the counter-value out
    /// of the fund and into escrow, where it is explicitly the user's until an
    /// operator sends it.
    /// </remarks>
    private async Task CommitSellAsync(Trade trade, CancellationToken cancellationToken)
    {
        var wallet = trade.WalletCurrency;
        var local = trade.LocalCurrency;

        List<PostingLeg> legs =
        [
            new PostingLeg(AccountRef.User(trade.UserId, wallet), -trade.Amount),
            new PostingLeg(AccountRef.SettlementFund(wallet), trade.Net),
            new PostingLeg(AccountRef.SettlementFund(local), -trade.Local),
            new PostingLeg(AccountRef.Escrow(local), trade.Local),
        ];

        // A fee that rounds to nothing is not charged, and must be an omitted
        // leg rather than a zero one: the ledger refuses a zero entry.
        if (trade.Fee.IsPositive) legs.Add(new PostingLeg(AccountRef.Fees(wallet), trade.Fee));

        try
        {
            var receipt = await _ledger.PostAsync(
                new PostingRequest(
                    Kind: "payment",
                    Legs: legs,
                    IdempotencyKey: CommitKey(trade.Id),
                    Metadata: Metadata(trade, "p2p_sell"),
                    Events:
                    [
                        new PendingEvent(
                            P2PEvents.Context,
                            P2PEvents.TradeCommitted,
                            new TradeCommitted(
                                trade.Id, trade.UserId, trade.MethodId,
                                trade.Amount, trade.Local, trade.Reference,
                                _clock.GetUtcNow())),
                    ]),
                cancellationToken).ConfigureAwait(false);

            trade.CommitPostingId = receipt.PostingId;
            await MarkCommittedAsync(trade.Id, receipt.PostingId, cancellationToken)
                .ConfigureAwait(false);
            trade.Status = P2PTradeStatuses.AwaitingPayout;
        }
        catch (InsufficientFundsException e)
        {
            // A refusal, not a failure: the ledger decided before writing
            // anything, so throwing the row away is safe.
            await DiscardQuietlyAsync(trade, cancellationToken).ConfigureAwait(false);

            var failure = new P2PException(
                P2PErrors.InsufficientFunds, 422,
                $"The account holds {e.Available} and {e.Requested} was requested.");
            failure.Facts["currency"] = wallet.Code;
            failure.Facts["available"] = e.Available.ToString();
            failure.Facts["requested"] = e.Requested.ToString();
            throw failure;
        }
        catch
        {
            // Anything else may have committed. Only undo when the ledger
            // confirms nothing was written; otherwise leave it pending for the
            // sweeper, which can tell the two apart later.
            await DiscardIfNothingPostedAsync(trade, cancellationToken).ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>Moves the money out, whichever way the trade ends.</summary>
    private async Task SettleAsync(Trade trade, CancellationToken cancellationToken)
    {
        var postingId = await PostSettlementAsync(trade, cancellationToken).ConfigureAwait(false);
        await FinishSettlementAsync(trade, postingId, cancellationToken).ConfigureAwait(false);
    }

    private async Task<Guid> PostSettlementAsync(Trade trade, CancellationToken cancellationToken)
    {
        var wallet = trade.WalletCurrency;
        var local = trade.LocalCurrency;
        var now = _clock.GetUtcNow();

        List<PostingLeg> legs;
        PendingEvent announcement;

        switch (trade.SettleIntent)
        {
            case P2PSettleIntents.Payout:
                // The local money leaves. Escrow clears against the mirror of
                // the rail: positive there means sent out, which is the same
                // convention a deposit uses in reverse.
                legs =
                [
                    new PostingLeg(AccountRef.Escrow(local), -trade.Local),
                    new PostingLeg(AccountRef.External(trade.MethodId, local), trade.Local),
                ];
                announcement = Completed(trade, now);
                break;

            case P2PSettleIntents.Credit:
                legs =
                [
                    new PostingLeg(AccountRef.External(trade.MethodId, local), -trade.Local),
                    new PostingLeg(AccountRef.SettlementFund(local), trade.Local),
                    new PostingLeg(AccountRef.SettlementFund(wallet), -trade.Amount),
                    new PostingLeg(AccountRef.User(trade.UserId, wallet), trade.Net),
                ];
                if (trade.Fee.IsPositive)
                {
                    legs.Add(new PostingLeg(AccountRef.Fees(wallet), trade.Fee));
                }

                announcement = Completed(trade, now);
                break;

            default:
                // A refund undoes the commit exactly, fee included. IslaPay
                // did not provide the service, so it does not keep the charge.
                legs =
                [
                    new PostingLeg(AccountRef.Escrow(local), -trade.Local),
                    new PostingLeg(AccountRef.SettlementFund(local), trade.Local),
                    new PostingLeg(AccountRef.SettlementFund(wallet), -trade.Net),
                    new PostingLeg(AccountRef.User(trade.UserId, wallet), trade.Amount),
                ];
                if (trade.Fee.IsPositive)
                {
                    legs.Add(new PostingLeg(AccountRef.Fees(wallet), -trade.Fee));
                }

                announcement = new PendingEvent(
                    P2PEvents.Context,
                    P2PEvents.TradeRefunded,
                    new TradeRefunded(
                        trade.Id, trade.UserId, trade.MethodId, trade.Amount,
                        trade.FailureReason ?? "Refunded.", now));
                break;
        }

        var receipt = await _ledger.PostAsync(
            new PostingRequest(
                Kind: "payment",
                Legs: legs,
                IdempotencyKey: SettleKey(trade.Id),
                Metadata: Metadata(trade, $"p2p_{trade.SettleIntent}"),
                Events: [announcement]),
            cancellationToken).ConfigureAwait(false);

        return receipt.PostingId;
    }

    private static PendingEvent Completed(Trade trade, DateTimeOffset now) => new(
        P2PEvents.Context,
        P2PEvents.TradeCompleted,
        new TradeCompleted(
            trade.Id, trade.UserId, ParseSide(trade.Side), trade.MethodId,
            trade.Amount, trade.Local, trade.OperatorReference ?? string.Empty, now));

    private async Task FinishSettlementAsync(
        Trade trade, Guid postingId, CancellationToken cancellationToken)
    {
        var outcome = trade.SettleIntent == P2PSettleIntents.Refund
            ? P2PTradeStatuses.Refunded
            : P2PTradeStatuses.Completed;

        var now = _clock.GetUtcNow();

        await _db.Database.ExecuteSqlAsync(
            $"""
             UPDATE p2p.trades
             SET status = {outcome}, settle_posting_id = {postingId}, settled_at = {now},
                 settle_intent = NULL
             WHERE id = {trade.Id} AND status = 'settling'
             """, cancellationToken).ConfigureAwait(false);

        _db.ChangeTracker.Clear();
        trade.Status = outcome;
        trade.SettlePostingId = postingId;
        trade.SettledAt = now;
        trade.SettleIntent = null;
    }

    private async Task RefundAsync(
        Trade trade, string reason, string? operatorId, CancellationToken cancellationToken)
    {
        if (trade.Status != P2PTradeStatuses.Settling)
        {
            await _db.Database.ExecuteSqlAsync(
                $"""
                 UPDATE p2p.trades
                 SET status = 'settling', settle_intent = 'refund',
                     failure_reason = {reason}, operator_id = {operatorId}
                 WHERE id = {trade.Id} AND status = 'awaiting_payout'
                 """, cancellationToken).ConfigureAwait(false);

            _db.ChangeTracker.Clear();
            trade.Status = P2PTradeStatuses.Settling;
            trade.SettleIntent = P2PSettleIntents.Refund;
            trade.FailureReason = reason;
            trade.OperatorId = operatorId;
        }

        await SettleAsync(trade, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Takes the trade row into <c>settling</c>, or says why it cannot.
    /// </summary>
    /// <remarks>
    /// The lock is what serialises an operator confirming against the same
    /// user cancelling. Resuming from <c>settling</c> with the same intent is
    /// deliberate: a retry after a crash should carry on rather than refuse,
    /// and the ledger key makes the repeated post a no-op.
    /// </remarks>
    private async Task<Trade> ClaimForSettlementAsync(
        Guid id,
        P2PSide side,
        string intent,
        string[] from,
        string operatorId,
        string reference,
        string? failureReason,
        CancellationToken cancellationToken)
    {
        await using var transaction = await _db.Database
            .BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        // See LockAsync: a tracked copy would shadow what the row actually says.
        _db.ChangeTracker.Clear();

        var rows = await _db.Trades.FromSql(
            $"SELECT * FROM p2p.trades WHERE id = {id} FOR UPDATE")
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        if (rows.Count == 0) throw new P2PException(P2PErrors.TradeNotFound, 404, "No such trade.");

        var trade = rows[0];

        if (ParseSide(trade.Side) != side)
        {
            throw new P2PException(
                P2PErrors.WrongSide, 409,
                $"That is a {trade.Side}; this settles a {Wire(side)}.");
        }

        // Confirming twice is not an error. The operator gets the same answer.
        if (trade.Status == P2PTradeStatuses.Completed) return trade;

        var resumable = trade.Status == P2PTradeStatuses.Settling && trade.SettleIntent == intent;

        if (!resumable && !from.Contains(trade.Status, StringComparer.Ordinal))
        {
            throw new P2PException(
                P2PErrors.TradeNotOpen, 409, "This trade is not waiting on that.");
        }

        if (!resumable)
        {
            trade.Status = P2PTradeStatuses.Settling;
            trade.SettleIntent = intent;
            trade.OperatorId = operatorId;
            trade.OperatorReference = reference;
            trade.FailureReason = failureReason;
            await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return trade;
    }

    private async Task<Trade?> LockAsync(Guid id, CancellationToken cancellationToken)
    {
        // The change tracker is cleared first, and this is not housekeeping.
        // EF's identity map wins over a re-read: an entity already tracked in
        // this context keeps the values it was loaded with, so a row another
        // transaction has since moved on would come back at its old status.
        // The whole point of reading under `FOR UPDATE` is to see what is
        // actually there — and the sweeper, which walks up to a hundred trades
        // on one context, is exactly where a stale copy would be acted on.
        _db.ChangeTracker.Clear();

        var rows = await _db.Trades.FromSql(
            $"SELECT * FROM p2p.trades WHERE id = {id} FOR UPDATE")
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        return rows.Count == 0 ? null : rows[0];
    }

    private async Task SetStatusAsync(
        Trade trade, string status, CancellationToken cancellationToken, bool clearIntent = false)
    {
        await _db.Database.ExecuteSqlAsync(
            $"""
             UPDATE p2p.trades
             SET status = {status},
                 commit_posting_id = {trade.CommitPostingId},
                 settle_intent = CASE WHEN {clearIntent} THEN NULL ELSE settle_intent END
             WHERE id = {trade.Id}
             """, cancellationToken).ConfigureAwait(false);

        _db.ChangeTracker.Clear();
        trade.Status = status;
        if (clearIntent) trade.SettleIntent = null;
    }

    private async Task MarkCommittedAsync(
        Guid id, Guid postingId, CancellationToken cancellationToken)
    {
        await _db.Database.ExecuteSqlAsync(
            $"""
             UPDATE p2p.trades
             SET status = 'awaiting_payout', commit_posting_id = {postingId}
             WHERE id = {id} AND status = 'pending'
             """, cancellationToken).ConfigureAwait(false);

        _db.ChangeTracker.Clear();
    }

    /// <summary>Removes a trade whose commit never posted.</summary>
    private async Task DiscardAsync(Trade trade, CancellationToken cancellationToken)
    {
        await _db.Database.ExecuteSqlAsync(
            $"DELETE FROM p2p.trades WHERE id = {trade.Id} AND status = 'pending'",
            cancellationToken).ConfigureAwait(false);

        _db.ChangeTracker.Clear();
    }

    private async Task DiscardQuietlyAsync(Trade trade, CancellationToken cancellationToken)
    {
        try
        {
            await DiscardAsync(trade, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception e) when (e is NpgsqlException or DbUpdateException or OperationCanceledException)
        {
        }
    }

    private async Task DiscardIfNothingPostedAsync(Trade trade, CancellationToken cancellationToken)
    {
        try
        {
            if (await _ledger.FindPostingAsync(CommitKey(trade.Id), cancellationToken)
                .ConfigureAwait(false) is null)
            {
                await DiscardAsync(trade, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            // Could not find out. Leaving it pending is the safe answer: the
            // sweeper asks again later.
        }
    }

    // ---------------------------------------------------------------- pricing

    /// <summary>
    /// What a trade costs and yields.
    /// </summary>
    /// <remarks>
    /// The fee is always a percentage of <paramref name="amount"/>, the
    /// wallet-currency gross, and is always taken on the wallet side. What
    /// crosses the currency boundary differs by side, and it has to:
    /// <list type="bullet">
    /// <item><description>
    /// Selling 100 E-ISLA at 120: IslaPay keeps 1 and converts the remaining
    /// 99, so the user receives 11,880 CUP.
    /// </description></item>
    /// <item><description>
    /// Buying with a 100 E-ISLA gross at 120: the user sends 12,000 CUP, which is
    /// worth 100 E-ISLA, and IslaPay takes 1 of them before crediting
    /// 99.
    /// </description></item>
    /// </list>
    /// Making both convert the same figure would mean earning nothing on one
    /// of the two directions.
    /// </remarks>
    private P2PQuoteDto Price(TradeMethod method, P2PSide side, Money amount, decimal rate)
    {
        var fee = amount.MultiplyByBasisPoints(_options.FeeBps, MidpointRounding.ToEven);
        var converted = side == P2PSide.Sell ? amount - fee : amount;
        var local = converted.ConvertTo(method.LocalCurrency, rate, MidpointRounding.ToEven);

        return new P2PQuoteDto(
            Side: side,
            MethodId: method.Id,
            MethodName: method.Name,
            Amount: amount,
            Fee: fee,
            Local: local,
            Rate: Format(rate)!,
            Executable: true,
            Reason: null);
    }

    private static P2PQuoteDto Unexecutable(TradeMethod method, P2PQuoteRequest request, string reason) =>
        new(
            Side: request.Side,
            MethodId: method.Id,
            MethodName: method.Name,
            Amount: request.Amount,
            Fee: Money.Zero(request.Amount.Currency),
            Local: Money.Zero(method.LocalCurrency),
            Rate: "0",
            Executable: false,
            Reason: reason);

    /// <summary>
    /// How far short the fund is, or null when it can cover the trade.
    /// </summary>
    /// <remarks>
    /// The settlement fund is a platform account and may go negative, so the
    /// ledger will happily record a payout IslaPay cannot make. This is the
    /// only thing standing between a published rate and a promise that cannot
    /// be kept.
    /// </remarks>
    private async Task<Money?> FundShortfallAsync(
        TradeMethod method, P2PSide side, P2PQuoteDto priced, CancellationToken cancellationToken)
    {
        // A sell spends the fund's local currency; a buy spends its wallet
        // currency. Each side is short of a different thing.
        var (account, needed) = side == P2PSide.Sell
            ? (AccountRef.SettlementFund(method.LocalCurrency), priced.Local)
            : (AccountRef.SettlementFund(priced.Amount.Currency), priced.Amount);

        var held = await _ledger.BalanceOfAsync(account, cancellationToken).ConfigureAwait(false);
        var reserve = Money.FromMinorUnits(_options.FundReserveMinor, needed.Currency);
        var available = held - reserve;

        return available >= needed ? null : needed - available;
    }

    private async Task<decimal?> CurrentRateAsync(
        TradeMethod method, P2PSide side, string walletCurrency, CancellationToken cancellationToken)
    {
        var wire = Wire(side);
        var now = _clock.GetUtcNow();

        // The newest row decides, and a withdrawn side's newest row has no
        // rate: that reads as "not offered", not as the price before it.
        var latest = await _db.Rates.AsNoTracking()
            .Where(r => r.MethodId == method.Id
                     && r.Side == wire
                     && r.WalletCurrencyCode == walletCurrency
                     && r.EffectiveFrom <= now)
            .OrderByDescending(r => r.EffectiveFrom)
            .ThenByDescending(r => r.Id)
            .Select(r => new { r.Rate })
            .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);

        return latest?.Rate;
    }

    /// <summary>
    /// Every rail's current prices, keyed by rail, holdable currencies only,
    /// in the catalogue's order.
    /// </summary>
    /// <remarks>
    /// One read of the rates table rather than one per rail, side and
    /// currency. Rates are set by hand, so the table stays small enough to
    /// fold in memory.
    /// </remarks>
    private async Task<Dictionary<string, IReadOnlyList<P2PMethodRateDto>>> RatesByMethodAsync(
        CancellationToken cancellationToken)
    {
        var now = _clock.GetUtcNow();
        var rows = await _db.Rates.AsNoTracking()
            .Where(r => r.EffectiveFrom <= now)
            .OrderByDescending(r => r.EffectiveFrom)
            .ThenByDescending(r => r.Id)
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        var current = new Dictionary<(string Method, string Side, string Wallet), decimal?>();
        foreach (var row in rows)
        {
            current.TryAdd((row.MethodId, row.Side, row.WalletCurrencyCode), row.Rate);
        }

        var result = new Dictionary<string, IReadOnlyList<P2PMethodRateDto>>(StringComparer.Ordinal);
        foreach (var method in current.Keys.Select(k => k.Method).Distinct(StringComparer.Ordinal))
        {
            var list = new List<P2PMethodRateDto>();
            foreach (var wallet in _catalog.Holdable)
            {
                var sell = current.GetValueOrDefault((method, "sell", wallet.Code));
                var buy = current.GetValueOrDefault((method, "buy", wallet.Code));
                if (sell is null && buy is null) continue;
                list.Add(new P2PMethodRateDto(wallet.Code, Format(sell), Format(buy)));
            }

            result[method] = list;
        }

        return result;
    }

    private static void RequireQuoteStillHolds(string? quoted, decimal current)
    {
        if (string.IsNullOrWhiteSpace(quoted)) return;

        if (!decimal.TryParse(
                quoted, NumberStyles.Number, CultureInfo.InvariantCulture, out var was)
            || was != current)
        {
            // Filling at a price the user never saw is worse than making them
            // look again.
            throw new P2PException(
                P2PErrors.QuoteExpired, 409, "The rate moved. Ask for a new quote.");
        }
    }

    /// <summary>
    /// The wallet side: a positive amount in a currency customers hold.
    /// </summary>
    /// <remarks>
    /// The catalogue's holdable set is the rule — today E-ISLA, USDT and USDC.
    /// A peso or a dollar here would be a trade of local money for local
    /// money, which is not what this desk does.
    /// </remarks>
    private void RequireTradeableAmount(Money amount)
    {
        if (!IsHoldable(amount.Currency.Code))
        {
            throw new P2PException(
                P2PErrors.InvalidAmount, 422,
                $"{amount.Currency.Code} is not a currency this market trades from a wallet.");
        }

        if (!amount.IsPositive)
        {
            throw new P2PException(
                P2PErrors.InvalidAmount, 422, "A trade must be for more than zero.");
        }
    }

    /// <summary>The local leg against the rail's limits, which are in that currency.</summary>
    private static void RequireWithinLimits(TradeMethod method, Money local)
    {
        if (local < method.Minimum)
        {
            var failure = new P2PException(
                P2PErrors.BelowMinimum, 422, $"The minimum is {method.Minimum}.");
            failure.Facts["minimum"] = method.Minimum.ToString();
            failure.Facts["currency"] = method.LocalCurrencyCode;
            throw failure;
        }

        if (local > method.Maximum)
        {
            var failure = new P2PException(
                P2PErrors.AboveMaximum, 422, $"The maximum is {method.Maximum}.");
            failure.Facts["maximum"] = method.Maximum.ToString();
            failure.Facts["currency"] = method.LocalCurrencyCode;
            throw failure;
        }
    }

    private bool IsHoldable(string code) =>
        _catalog.Holdable.Any(c => string.Equals(c.Code, code, StringComparison.Ordinal));

    private async Task<TradeMethod> RequireMethodAsync(
        string methodId, CancellationToken cancellationToken) =>
        await _db.Methods.AsNoTracking()
            .FirstOrDefaultAsync(m => m.Id == methodId, cancellationToken).ConfigureAwait(false)
        ?? throw new P2PException(P2PErrors.MethodNotFound, 404, "No such rail.");

    private static string RequireReference(string? reference) =>
        string.IsNullOrWhiteSpace(reference)
            // Without it a dispute six weeks later has nothing to check.
            ? throw new P2PException(
                P2PErrors.InvalidAmount, 422, "A settlement needs the bank's reference.")
            : reference.Trim();

    private static string RequireReason(string? reason) =>
        string.IsNullOrWhiteSpace(reason)
            ? throw new P2PException(
                P2PErrors.InvalidAmount, 422, "A failed payout needs a reason the user can read.")
            : reason.Trim();

    private static Dictionary<string, string> Metadata(Trade trade, string what) => new(StringComparer.Ordinal)
    {
        ["tradeId"] = trade.Id.ToString("D", CultureInfo.InvariantCulture),
        ["reference"] = trade.Reference,
        ["method"] = trade.MethodName,
        ["kind"] = what,
        ["rate"] = Format(trade.Rate)!,
        ["local"] = trade.Local.ToString(),
    };

    private async Task<P2PTradeDto> ProjectAsync(Trade trade, CancellationToken cancellationToken)
    {
        // Only a buy needs instructions, and only while it is still expected.
        string? instructions = null;
        if (ParseSide(trade.Side) == P2PSide.Buy
            && trade.Status is P2PTradeStatuses.AwaitingPayment or P2PTradeStatuses.Expired)
        {
            instructions = await _db.Methods.AsNoTracking()
                .Where(m => m.Id == trade.MethodId)
                .Select(m => m.Instructions)
                .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        }

        return new P2PTradeDto(
            Id: trade.Id.ToString("D", CultureInfo.InvariantCulture),
            Side: ParseSide(trade.Side),
            MethodId: trade.MethodId,
            MethodName: trade.MethodName,
            Amount: trade.Amount,
            Fee: trade.Fee,
            Local: trade.Local,
            Rate: Format(trade.Rate)!,
            Status: trade.Status,
            Reference: trade.Reference,
            Instructions: string.IsNullOrWhiteSpace(instructions) ? null : instructions,
            FailureReason: trade.FailureReason,
            CreatedAt: trade.CreatedAt,
            ExpiresAt: trade.ExpiresAt,
            SettledAt: trade.SettledAt,
            PayoutTo: trade.PayoutTo);
    }

    private static P2PQueueItemDto OperatorView(Trade t, DateTimeOffset now) => new(
        Id: t.Id.ToString("D", CultureInfo.InvariantCulture),
        Side: ParseSide(t.Side),
        MethodId: t.MethodId,
        MethodName: t.MethodName,
        UserId: t.UserId,
        UserName: t.UserName,
        Amount: t.Amount,
        Local: t.Local,
        Status: t.Status,
        Reference: t.Reference,
        CreatedAt: t.CreatedAt,
        Waiting: (t.SettledAt ?? now) - t.CreatedAt,
        PayoutTo: t.PayoutTo,
        ExpiresAt: t.ExpiresAt,
        SettledAt: t.SettledAt,
        OperatorReference: t.OperatorReference,
        FailureReason: t.FailureReason);

    private static P2PAdminMethodDto AdminView(
        TradeMethod m, IReadOnlyList<P2PMethodRateDto> rates) => new(
        Id: m.Id,
        Name: m.Name,
        Code: m.LocalCurrencyCode,
        Available: m.Available,
        Instructions: m.Instructions,
        Minimum: m.Minimum,
        Maximum: m.Maximum,
        Rates: rates,
        UpdatedAt: m.UpdatedAt);

    /// <summary>
    /// The local side of a new rail: a fiat currency, switched on, that no
    /// customer holds.
    /// </summary>
    private Currency RequireLocalCurrency(string? code)
    {
        var info = _catalog.Describe(code?.Trim().ToUpperInvariant());
        if (info is null || !info.Enabled)
        {
            throw new P2PException(
                P2PErrors.InvalidCurrency, 422,
                $"'{code}' is not a currency the catalogue has switched on. Enable it there first.");
        }

        if (!string.Equals(info.Kind, CurrencyKinds.Fiat, StringComparison.Ordinal) || info.CustomerHoldable)
        {
            throw new P2PException(
                P2PErrors.InvalidCurrency, 422,
                $"{info.Code} is held in wallets; a rail's local side is money paid outside IslaPay.");
        }

        return info.Currency;
    }

    private static string RequireInstructions(string? instructions)
    {
        var text = (instructions ?? string.Empty).Trim();
        if (text.Length > MaximumInstructionsLength)
        {
            throw new P2PException(
                P2PErrors.InvalidInstructions, 422,
                $"Instructions are limited to {MaximumInstructionsLength} characters.");
        }

        return text;
    }

    private static (Money Minimum, Money Maximum) RequireLimits(
        Currency local, Money minimum, Money maximum)
    {
        if (minimum.Currency != local || maximum.Currency != local)
        {
            throw new P2PException(
                P2PErrors.InvalidLimits, 422, $"A rail's limits are in its own currency, {local.Code}.");
        }

        if (!minimum.IsPositive || maximum < minimum)
        {
            throw new P2PException(
                P2PErrors.InvalidLimits, 422,
                "The minimum must be above zero and no larger than the maximum.");
        }

        return (minimum, maximum);
    }

    private static string RequireMethodName(string name)
    {
        var text = name.Trim();
        if (text.Length == 0 || text.Length > MaximumMethodNameLength)
        {
            throw new P2PException(
                P2PErrors.InvalidMethodName, 422,
                $"A rail's name is between 1 and {MaximumMethodNameLength} characters.");
        }

        return text;
    }

    private static string Wire(P2PSide side) => side == P2PSide.Sell ? "sell" : "buy";

    private static P2PSide ParseSide(string side) =>
        string.Equals(side, "sell", StringComparison.Ordinal) ? P2PSide.Sell : P2PSide.Buy;

    /// <summary>
    /// A rate as the wire carries it: a plain decimal string, trimmed of
    /// trailing zeros so "380" does not arrive as "380.00000000".
    /// </summary>
    private static string? Format(decimal? rate) => rate is null
        ? null
        : (rate.Value == decimal.Truncate(rate.Value)
            ? decimal.Truncate(rate.Value).ToString(CultureInfo.InvariantCulture)
            : rate.Value.ToString("0.########", CultureInfo.InvariantCulture));

    private static bool IsUniqueViolation(DbUpdateException e) =>
        e.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation };

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
            return null;
        }
    }
}
