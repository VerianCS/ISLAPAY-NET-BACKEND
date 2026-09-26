using IslaPay.Catalog.Contracts;
using IslaPay.Custody.Contracts;
using IslaPay.Identity.Contracts;
using IslaPay.Ledger.Contracts;
using IslaPay.Platform;
using Microsoft.EntityFrameworkCore;

namespace IslaPay.Custody;

/// <summary>What the scanner saw, in the shape this module takes it in.</summary>
/// <param name="OutputIndex">
/// Which payment within the transaction. A transaction can pay the same
/// address twice, and those are two deposits.
/// </param>
public sealed record ObservedTransfer(
    string Network,
    string Address,
    string TxHash,
    Money Amount,
    int Confirmations,
    int OutputIndex = 0);

/// <summary>
/// Deposit addresses, and what arrives at them.
/// </summary>
/// <remarks>
/// <para>
/// The rule the whole module exists to keep: <b>nothing reaches the ledger
/// before the chain is final</b>. A transfer four blocks deep can still be
/// un-happened, and crediting it early leaves IslaPay clawing a balance back
/// from somebody who has already spent it.
/// </para>
/// <para>
/// The second rule is that crediting happens exactly once. This module and the
/// ledger commit separately — the ledger owns its transaction and will not
/// hand it out — so there is a window where the posting exists and this
/// module's row does not know. The shape is the same one Marketplace and P2P
/// use: an in-flight status that means "a posting for this may exist", one
/// idempotency key, and a sweeper that asks the ledger what actually happened
/// rather than guessing.
/// </para>
/// </remarks>
public sealed class CustodyService
{
    private readonly CustodyDbContext _db;
    private readonly ILedger _ledger;
    private readonly ICurrencyCatalog _catalog;
    private readonly IUserDirectory _directory;
    private readonly IDepositAddresses _addresses;
    private readonly TimeProvider _clock;

    public CustodyService(
        CustodyDbContext db,
        ILedger ledger,
        ICurrencyCatalog catalog,
        IUserDirectory directory,
        IDepositAddresses addresses,
        TimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(ledger);
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(directory);
        ArgumentNullException.ThrowIfNull(addresses);
        ArgumentNullException.ThrowIfNull(clock);
        _db = db;
        _ledger = ledger;
        _catalog = catalog;
        _directory = directory;
        _addresses = addresses;
        _clock = clock;
    }

    /// <summary>
    /// The one key a deposit's credit is ever written under.
    /// </summary>
    /// <remarks>
    /// Derived from the deposit id, so a retry from anywhere — the scanner
    /// seeing the block again, the sweeper finishing an interrupted credit,
    /// an operator re-running something by hand — collides on the ledger's
    /// unique index rather than paying twice.
    /// </remarks>
    public static string CreditKey(Guid depositId) =>
        $"custody:credit:{depositId}";

    // ----------------------------------------------------------- addresses

    /// <summary>
    /// The caller's address for one asset on one chain, issuing one the first
    /// time.
    /// </summary>
    /// <remarks>
    /// <para>
    /// An asset <i>and</i> a chain, because a chain carries several assets and
    /// an asset lives on several chains. Asking for "a TRON address" was only
    /// ever coherent while the enum pinned one currency to each network, and
    /// the day USDC joins USDT on TRON that question has two right answers.
    /// </para>
    /// <para>
    /// Behind the same phone gate as every other way money moves. A deposit
    /// address is not a payment, but it is the thing that attributes incoming
    /// money to a person, and handing one out to an account nobody has proved
    /// is how a service becomes a laundry.
    /// </para>
    /// </remarks>
    public async Task<DepositAddressDto> AddressAsync(
        string userId, string currencyCode, string networkId,
        CancellationToken cancellationToken = default)
    {
        // Two different refusals, because they mean different things to
        // whoever is reading them. A chain nobody has heard of is a 404: the
        // client asked for something that does not exist. A chain that exists
        // and does not carry this asset — or carries it and is switched off —
        // is a 422: the request was well formed and the answer is no.
        var known = _catalog.Networks.Any(n =>
            string.Equals(n.Id, networkId?.Trim(), StringComparison.OrdinalIgnoreCase));
        if (!known) throw CustodyException.UnknownNetwork(networkId);

        var network = _catalog.OnNetwork(currencyCode, networkId);
        if (network is not { Enabled: true })
        {
            throw CustodyException.CurrencyNotOnNetwork(currencyCode, networkId);
        }

        await RequireVerifiedPhoneAsync(userId, cancellationToken).ConfigureAwait(false);

        var currency = _catalog.Require(network.CurrencyCode);
        var code = currency.Code;
        var id = network.NetworkId;

        var existing = await _db.Addresses
            .AsNoTracking()
            .SingleOrDefaultAsync(
                a => a.UserId == userId && a.Network == id && a.CurrencyCode == code,
                cancellationToken)
            .ConfigureAwait(false);

        if (existing is not null) return Project(existing, network);

        var issued = await _addresses.IssueAsync(userId, network, cancellationToken)
            .ConfigureAwait(false);

        // Checked here rather than trusted. This module cannot verify that a
        // key exists behind an address, but it can refuse to show one that is
        // not even the right shape for the network — and an address is the one
        // string in this system where being wrong is unrecoverable.
        if (!AddressFormat.Matches(network, issued.Address))
        {
            throw CustodyException.AddressUnavailable(
                $"The custodian returned '{issued.Address}', which is not a "
                + $"{network.NetworkName} address.");
        }

        var row = new DepositAddress
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            Network = id,
            CurrencyCode = code,
            CurrencyScale = currency.Scale,
            Address = issued.Address,
            CustodianRef = issued.CustodianRef,
            IssuedAt = _clock.GetUtcNow(),
        };

        _db.Addresses.Add(row);

        try
        {
            await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateException)
        {
            // Two requests raced and the unique index settled it. Whichever
            // row won is the user's address; issuing a second one would leave
            // money arriving at an address nothing is watching.
            _db.ChangeTracker.Clear();
            var winner = await _db.Addresses
                .AsNoTracking()
                .SingleAsync(
                    a => a.UserId == userId && a.Network == id && a.CurrencyCode == code,
                    cancellationToken)
                .ConfigureAwait(false);
            return Project(winner, network);
        }

        return Project(row, network);
    }

    // ------------------------------------------------------------ deposits

    /// <summary>The caller's deposits, newest first.</summary>
    public async Task<IReadOnlyList<DepositDto>> DepositsAsync(
        string userId, int limit = 20, CancellationToken cancellationToken = default)
    {
        var rows = await _db.Deposits
            .AsNoTracking()
            .Where(d => d.UserId == userId)
            .OrderByDescending(d => d.FirstSeenAt)
            .Take(Math.Clamp(limit, 1, 100))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return [.. rows.Select(Project)];
    }

    /// <summary>
    /// Records what the scanner saw, and credits it if the chain is done.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Safe to call with the same transfer any number of times, which is not a
    /// nicety: a scanner re-reads blocks, restarts mid-range, and gets run
    /// twice by accident. Seeing a transfer a hundred times has to be
    /// indistinguishable from seeing it once, and the unique index on
    /// (network, tx hash, output) is what makes that true rather than hoped
    /// for.
    /// </para>
    /// <para>
    /// A transfer to an address this build does not know is ignored and not an
    /// error. Somebody sending to a stale address, or a scanner watching more
    /// than it needs to, is not a reason to stop processing a block.
    /// </para>
    /// </remarks>
    /// <returns>The deposit, or null when the address is not one of ours.</returns>
    public async Task<DepositDto?> ObserveAsync(
        ObservedTransfer seen, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(seen);

        var code = seen.Amount.Currency.Code;
        var network = _catalog.OnNetwork(code, seen.Network)
            ?? throw CustodyException.CurrencyNotOnNetwork(code, seen.Network);

        // The scanner states a scale along with the amount, and a scale that
        // disagrees with the catalogue is not a rounding difference: it is a
        // credit wrong by a factor of ten thousand. The catalogue is the one
        // that decides, and a database trigger stops it ever changing.
        var currency = _catalog.Require(code);
        if (seen.Amount.Currency.Scale != currency.Scale)
        {
            throw new ArgumentException(
                $"{code} is accounted in {currency.Scale} decimal places and the "
                + $"transfer states {seen.Amount.Currency.Scale}.",
                nameof(seen));
        }

        if (!seen.Amount.IsPositive)
        {
            // A zero or negative transfer is not a deposit. It is a scanner
            // bug, and swallowing it would hide the bug.
            throw new ArgumentException(
                $"A deposit must be positive; saw {seen.Amount}.", nameof(seen));
        }

        var address = await _db.Addresses
            .AsNoTracking()
            .SingleOrDefaultAsync(
                a => a.Network == network.NetworkId && a.Address == seen.Address,
                cancellationToken)
            .ConfigureAwait(false);

        if (address is null) return null;

        var deposit = await FindOrRecordAsync(seen, network, address, cancellationToken)
            .ConfigureAwait(false);

        return await AdvanceAsync(deposit.Id, seen.Confirmations, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Marks a deposit as gone from the chain.
    /// </summary>
    /// <remarks>
    /// Only meaningful before finality, and the caller is the scanner, which
    /// notices that a transfer it had seen is no longer in the chain it is
    /// reading. Nothing was posted, so there is nothing to reverse; the row
    /// stays so the user is not left wondering where the deposit they watched
    /// went.
    /// <para>
    /// A credited deposit is never orphaned here. If a chain reorganised below
    /// something already treated as final, the loss is real and it is an
    /// operator's problem, not a status change.
    /// </para>
    /// </remarks>
    public async Task<bool> OrphanAsync(
        string network, string txHash, int outputIndex = 0,
        CancellationToken cancellationToken = default)
    {
        _db.ChangeTracker.Clear();

        var deposit = await _db.Deposits
            .SingleOrDefaultAsync(
                d => d.Network == network
                     && d.TxHash == txHash
                     && d.OutputIndex == outputIndex,
                cancellationToken)
            .ConfigureAwait(false);

        if (deposit is null || deposit.Status != DepositStatuses.Confirming) return false;

        deposit.Status = DepositStatuses.Orphaned;
        deposit.UpdatedAt = _clock.GetUtcNow();
        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return true;
    }

    // ------------------------------------------------------------- crediting

    /// <summary>
    /// Moves a deposit forward to where the chain says it is.
    /// </summary>
    /// <remarks>
    /// The row is locked for the read, because two scanner passes arriving at
    /// once must not both decide to credit. Confirmations only ever go up:
    /// a scanner reading an older block should not walk a deposit backwards.
    /// </remarks>
    private async Task<DepositDto> AdvanceAsync(
        Guid depositId, int confirmations, CancellationToken cancellationToken)
    {
        // EF's identity map would hand back the copy read a moment ago, which
        // is exactly the stale row the lock exists to avoid.
        _db.ChangeTracker.Clear();

        var deposit = await _db.Deposits
            .FromSql($"SELECT * FROM custody.deposits WHERE id = {depositId} FOR UPDATE")
            .SingleAsync(cancellationToken)
            .ConfigureAwait(false);

        if (confirmations > deposit.Confirmations)
        {
            deposit.Confirmations = confirmations;
            deposit.UpdatedAt = _clock.GetUtcNow();
            await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }

        // Found in flight. The next scanner pass is as good a moment to
        // resolve it as the sweeper's, and a much earlier one: the sweeper
        // waits out a grace period first, and a deposit sitting in
        // `crediting` is somebody's money not in their balance.
        if (deposit.Status == DepositStatuses.Crediting)
        {
            return await ResolveInFlightAsync(deposit.Id, cancellationToken)
                .ConfigureAwait(false);
        }

        if (deposit.Status != DepositStatuses.Confirming || !deposit.IsFinal)
        {
            return Project(deposit);
        }

        return await CreditAsync(deposit, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Finishes one deposit that was interrupted between the two commits.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The only safe way to close that window, and it decides nothing on its
    /// own: it asks the ledger whether the posting exists and then makes the
    /// row say what is already true. The two plausible guesses are each
    /// catastrophic in one direction — assuming a credit landed when it did
    /// not leaves somebody permanently short of money they really sent;
    /// assuming it did not credits the same chain transfer twice.
    /// </para>
    /// <para>
    /// When no posting landed, it does not merely release the deposit and hope
    /// somebody comes back for it. A deposit that is already final and deep is
    /// one a scanner has no reason to report again, so releasing it without
    /// finishing it would leave it waiting forever. It is credited here, under
    /// the same key, which is what makes doing so safe even if the earlier
    /// attempt turns out to have landed after all.
    /// </para>
    /// </remarks>
    private async Task<DepositDto> ResolveInFlightAsync(
        Guid depositId, CancellationToken cancellationToken)
    {
        var posting = await _ledger
            .FindPostingAsync(CreditKey(depositId), cancellationToken)
            .ConfigureAwait(false);

        if (posting is { } postingId)
        {
            return await SettleAsync(depositId, postingId, _clock.GetUtcNow(), cancellationToken)
                .ConfigureAwait(false);
        }

        await ReleaseAsync(depositId, cancellationToken).ConfigureAwait(false);

        _db.ChangeTracker.Clear();
        var deposit = await _db.Deposits
            .FromSql($"SELECT * FROM custody.deposits WHERE id = {depositId} FOR UPDATE")
            .SingleAsync(cancellationToken)
            .ConfigureAwait(false);

        return deposit is { Status: DepositStatuses.Confirming, IsFinal: true }
            ? await CreditAsync(deposit, cancellationToken).ConfigureAwait(false)
            : Project(deposit);
    }

    /// <summary>
    /// Credits a final deposit, once.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Three commits in a fixed order, and the order is the whole design.
    /// First this module records that it is about to ask — <c>crediting</c>,
    /// which means "a posting for this may exist". Then the ledger posts. Then
    /// this module records that it did.
    /// </para>
    /// <para>
    /// Dying between the first and the second leaves a deposit claiming a
    /// posting that was never written; dying between the second and the third
    /// leaves one whose money has moved without the row knowing. Both are the
    /// sweeper's to resolve, and it resolves them by asking the ledger rather
    /// than by assuming either way.
    /// </para>
    /// </remarks>
    private async Task<DepositDto> CreditAsync(
        Deposit deposit, CancellationToken cancellationToken)
    {
        deposit.Status = DepositStatuses.Crediting;
        deposit.UpdatedAt = _clock.GetUtcNow();
        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        var amount = deposit.Amount;
        var at = _clock.GetUtcNow();

        var receipt = await _ledger.PostAsync(
            new PostingRequest(
                Kind: "settlement",
                Legs:
                [
                    // The mirror convention: negative is money received from
                    // outside. The chain held it and now IslaPay owes it.
                    new PostingLeg(AccountRef.External(deposit.Network, amount.Currency), -amount),
                    new PostingLeg(AccountRef.User(deposit.UserId, amount.Currency), amount),
                ],
                IdempotencyKey: CreditKey(deposit.Id),
                Metadata: new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["method"] = deposit.Network,
                    ["txHash"] = deposit.TxHash,
                    ["confirmations"] = deposit.Confirmations.ToString(
                        System.Globalization.CultureInfo.InvariantCulture),
                },
                Events:
                [
                    new PendingEvent(
                        "custody",
                        CustodyEvents.DepositCredited,
                        new DepositCredited(
                            deposit.Id,
                            deposit.UserId,
                            deposit.Network,
                            deposit.TxHash,
                            amount,
                            Guid.Empty,
                            at)),
                ]),
            cancellationToken).ConfigureAwait(false);

        return await SettleAsync(deposit.Id, receipt.PostingId, at, cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<DepositDto> SettleAsync(
        Guid depositId, Guid postingId, DateTimeOffset at, CancellationToken cancellationToken)
    {
        _db.ChangeTracker.Clear();

        var deposit = await _db.Deposits
            .FromSql($"SELECT * FROM custody.deposits WHERE id = {depositId} FOR UPDATE")
            .SingleAsync(cancellationToken)
            .ConfigureAwait(false);

        if (deposit.Status == DepositStatuses.Credited) return Project(deposit);

        deposit.Status = DepositStatuses.Credited;
        deposit.CreditPostingId = postingId;
        deposit.CreditedAt = at;
        deposit.UpdatedAt = at;
        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return Project(deposit);
    }

    /// <summary>
    /// Finishes every deposit that has been in flight too long.
    /// </summary>
    /// <remarks>
    /// The backstop, not the main path: a scanner pass over the same deposit
    /// resolves it sooner. This exists for the ones nothing is looking at any
    /// more — a transfer whose block the scanner has long since moved past.
    /// </remarks>
    public async Task<int> RepairAsync(
        TimeSpan olderThan, CancellationToken cancellationToken = default)
    {
        var cutoff = _clock.GetUtcNow() - olderThan;

        var stuck = await _db.Deposits
            .AsNoTracking()
            .Where(d => d.Status == DepositStatuses.Crediting && d.UpdatedAt < cutoff)
            .OrderBy(d => d.UpdatedAt)
            .Take(100)
            .Select(d => d.Id)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        foreach (var id in stuck)
        {
            await ResolveInFlightAsync(id, cancellationToken).ConfigureAwait(false);
        }

        return stuck.Count;
    }

    private async Task ReleaseAsync(Guid depositId, CancellationToken cancellationToken)
    {
        _db.ChangeTracker.Clear();

        var deposit = await _db.Deposits
            .FromSql($"SELECT * FROM custody.deposits WHERE id = {depositId} FOR UPDATE")
            .SingleAsync(cancellationToken)
            .ConfigureAwait(false);

        if (deposit.Status != DepositStatuses.Crediting) return;

        deposit.Status = DepositStatuses.Confirming;
        deposit.UpdatedAt = _clock.GetUtcNow();
        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    // -------------------------------------------------------------- plumbing

    private async Task<Deposit> FindOrRecordAsync(
        ObservedTransfer seen,
        CurrencyOnNetwork network,
        DepositAddress address,
        CancellationToken cancellationToken)
    {
        var existing = await _db.Deposits
            .AsNoTracking()
            .SingleOrDefaultAsync(
                d => d.Network == network.NetworkId
                     && d.TxHash == seen.TxHash
                     && d.OutputIndex == seen.OutputIndex,
                cancellationToken)
            .ConfigureAwait(false);

        if (existing is not null) return existing;

        var now = _clock.GetUtcNow();
        var deposit = new Deposit
        {
            Id = Guid.NewGuid(),
            UserId = address.UserId,
            AddressId = address.Id,
            Network = network.NetworkId,
            CurrencyCode = seen.Amount.Currency.Code,
            CurrencyScale = seen.Amount.Currency.Scale,
            TxHash = seen.TxHash,
            OutputIndex = seen.OutputIndex,
            AmountMinor = seen.Amount.MinorUnits,
            // Zero here, not the scanner's figure: the row is created first
            // and advanced under a lock, so that a new deposit and a deeper
            // one take exactly the same path to being credited.
            Confirmations = 0,
            RequiredConfirmations = network.Confirmations,
            Status = DepositStatuses.Confirming,
            FirstSeenAt = now,
            UpdatedAt = now,
        };

        _db.Deposits.Add(deposit);

        try
        {
            await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return deposit;
        }
        catch (DbUpdateException)
        {
            // Two passes over the same block, at once. The unique index is
            // what makes that safe, and this is it doing its job.
            _db.ChangeTracker.Clear();
            return await _db.Deposits
                .AsNoTracking()
                .SingleAsync(
                    d => d.Network == network.NetworkId
                         && d.TxHash == seen.TxHash
                         && d.OutputIndex == seen.OutputIndex,
                    cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private async Task RequireVerifiedPhoneAsync(
        string userId, CancellationToken cancellationToken)
    {
        var user = await _directory.FindByIdAsync(userId, cancellationToken).ConfigureAwait(false);
        if (user is null || !user.PhoneVerified) throw CustodyException.PhoneNotVerified();

        // A frozen account is given no new place to send money to. What
        // arrives at an address it already has is still credited: a chain
        // cannot be told no, and the freeze is on moving money, not on owning it.
        if (Standing.Check(user) is { } refusal)
        {
            throw new CustodyException(refusal.Code, refusal.Status, refusal.Message) { Meta = refusal.Facts };
        }
    }

    private static DepositAddressDto Project(DepositAddress row, CurrencyOnNetwork network) =>
        new(
            Network: row.Network,
            NetworkName: network.NetworkName,
            Currency: row.CurrencyCode,
            Address: row.Address,
            Confirmations: network.Confirmations);

    private static DepositDto Project(Deposit row) =>
        new(
            Id: row.Id.ToString(),
            Network: row.Network,
            Address: string.Empty,
            TxHash: row.TxHash,
            Amount: row.Amount,
            Status: row.Status,
            Confirmations: row.Confirmations,
            RequiredConfirmations: row.RequiredConfirmations,
            FirstSeenAt: row.FirstSeenAt,
            CreditedAt: row.CreditedAt);
}
