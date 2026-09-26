using System.Globalization;
using System.Text.RegularExpressions;
using IslaPay.Catalog.Contracts;
using IslaPay.Ledger.Contracts;
using IslaPay.Platform;
using IslaPay.Platform.AspNet;
using IslaPay.Treasury.Contracts;
using Microsoft.AspNetCore.Http;

namespace IslaPay.Treasury;

/// <summary>
/// Where the money is, whether the books agree, and the one way more gets in.
/// </summary>
/// <remarks>
/// <para>
/// The treasury owns no table and no money. Everything it reports it asks
/// somebody else for — the ledger for balances and history, each module for
/// what it believes it is holding — and the one thing it writes, a funding
/// credit, it writes through <see cref="ILedger"/> like every other caller.
/// That is deliberate: a treasury with its own copy of the figures is a second
/// set of books, and the whole point of this module is to be the place where
/// the first set is checked.
/// </para>
/// <para>
/// It also references no module except through contracts, and it does not
/// reference Marketplace or P2P at all: the escrow claims arrive as
/// <see cref="IEscrowReporter"/> implementations the host registered, and this
/// class does not know what they are. Adding a fourth context with escrow is a
/// line in that module, not a change here.
/// </para>
/// </remarks>
public sealed partial class TreasuryService
{
    /// <summary>The platform accounts money may be credited into.</summary>
    /// <remarks>
    /// Not escrow: escrow holds money against a named order, and creating some
    /// without one is exactly the gap the reconciliation below exists to find.
    /// Not fees either — revenue is earned by a posting that had a payer, and
    /// a fee account somebody can top up is a revenue figure that means
    /// nothing.
    /// </remarks>
    private static readonly string[] CreditableDestinations = ["float", "settlement_fund"];

    private readonly ILedger _ledger;
    private readonly ICurrencyCatalog _catalog;
    private readonly IReadOnlyList<IEscrowReporter> _reporters;
    private readonly TimeProvider _clock;

    public TreasuryService(
        ILedger ledger,
        ICurrencyCatalog catalog,
        IEnumerable<IEscrowReporter> reporters,
        TimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(ledger);
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(reporters);
        ArgumentNullException.ThrowIfNull(clock);

        _ledger = ledger;
        _catalog = catalog;
        _reporters = [.. reporters];
        _clock = clock;
    }

    /// <summary>Every account IslaPay holds in its own name.</summary>
    public async Task<TreasuryBalancesDto> BalancesAsync(CancellationToken cancellationToken = default)
    {
        var balances = await _ledger.HouseBalancesAsync(cancellationToken).ConfigureAwait(false);

        return new TreasuryBalancesDto(
            _clock.GetUtcNow(),
            [.. balances.Select(b => new TreasuryAccountDto(
                NameOf(b.Account.Owner),
                b.Account.Owner == AccountOwner.External ? b.Account.Id : null,
                b.Balance,
                b.EntryCount))]);
    }

    /// <summary>
    /// What escrow holds, against what the modules say it should.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The currencies checked are the union of the two sides, not either one
    /// alone. Taking only the ledger's would miss a module claiming a hold in
    /// a currency escrow has nothing in — which is the more alarming of the
    /// two failures. Taking only the modules' would miss money sitting in
    /// escrow that nobody has a reason for, which is the other one.
    /// </para>
    /// <para>
    /// Every reporter is asked, and one that throws is not caught here: a
    /// reconciliation that quietly leaves out the context whose database was
    /// down would report agreement, and reporting agreement is the one thing
    /// this must never do wrongly.
    /// </para>
    /// </remarks>
    public async Task<TreasuryReconciliationDto> ReconciliationAsync(
        CancellationToken cancellationToken = default)
    {
        var claims = new Dictionary<string, List<(string Context, EscrowHolding Holding)>>(
            StringComparer.OrdinalIgnoreCase);

        foreach (var reporter in _reporters)
        {
            var holdings = await reporter.OutstandingAsync(cancellationToken).ConfigureAwait(false);
            foreach (var holding in holdings)
            {
                if (!claims.TryGetValue(holding.CurrencyCode, out var list))
                {
                    claims[holding.CurrencyCode] = list = [];
                }

                list.Add((reporter.Context, holding));
            }
        }

        var house = await _ledger.HouseBalancesAsync(cancellationToken).ConfigureAwait(false);
        var escrow = house
            .Where(b => b.Account.Owner == AccountOwner.Escrow)
            .ToDictionary(b => b.Account.Currency.Code, b => b.Balance, StringComparer.OrdinalIgnoreCase);

        var codes = escrow.Keys.Union(claims.Keys, StringComparer.OrdinalIgnoreCase)
            .OrderBy(c => c, StringComparer.Ordinal);

        var rows = new List<EscrowReconciliationDto>();
        foreach (var code in codes)
        {
            // Describe rather than Require: a currency that has been switched
            // off still has money in escrow, and refusing to report on it
            // would hide the balance at the exact moment somebody needs to see
            // it.
            var currency = _catalog.Describe(code)?.Currency
                ?? throw new TreasuryException(
                    TreasuryErrors.UnknownAccount, StatusCodes.Status500InternalServerError,
                    $"escrow holds '{code}', which the catalogue has no row for.");

            var ledger = escrow.TryGetValue(code, out var held)
                ? held
                : Money.FromMinorUnits(0, currency);

            var claimed = 0L;
            var inFlight = 0L;
            var breakdown = new List<EscrowClaimDto>();

            foreach (var (context, holding) in claims.GetValueOrDefault(code) ?? [])
            {
                claimed += holding.MinorUnits;
                inFlight += holding.InFlightMinorUnits;
                breakdown.Add(new EscrowClaimDto(
                    context,
                    Money.FromMinorUnits(holding.MinorUnits, currency),
                    Money.FromMinorUnits(holding.InFlightMinorUnits, currency)));
            }

            // The band: below it escrow is short, above it escrow holds money
            // nobody claims, and inside it the two agree as far as anything
            // can agree while a posting is in flight.
            var difference =
                ledger.MinorUnits < claimed ? ledger.MinorUnits - claimed
                : ledger.MinorUnits > claimed + inFlight ? ledger.MinorUnits - (claimed + inFlight)
                : 0L;

            rows.Add(new EscrowReconciliationDto(
                Currency: code,
                Ledger: ledger,
                Claimed: Money.FromMinorUnits(claimed, currency),
                InFlight: Money.FromMinorUnits(inFlight, currency),
                Difference: Money.FromMinorUnits(difference, currency),
                Balanced: difference == 0,
                Claims: [.. breakdown.OrderBy(c => c.Context, StringComparer.Ordinal)]));
        }

        return new TreasuryReconciliationDto(
            _clock.GetUtcNow(), rows.TrueForAll(r => r.Balanced), rows);
    }

    /// <summary>One account's history, newest first.</summary>
    /// <param name="owner">
    /// A platform account's name, or <c>external:&lt;mirror&gt;</c>.
    /// </param>
    public Task<LedgerEntryPage> EntriesAsync(
        string owner, string currencyCode, int limit, string? cursor,
        CancellationToken cancellationToken = default)
    {
        var currency = _catalog.Describe(currencyCode)?.Currency
            ?? throw new UnknownCurrencyException(currencyCode, "it is not in the catalogue.");

        return _ledger.AccountEntriesAsync(
            AccountFor(owner, currency), limit, cursor, cancellationToken);
    }

    /// <summary>
    /// Puts money into the system, against the place it came from.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The only door. Everything else in IslaPay moves money that is already
    /// here — a transfer, a fee, a conversion — and each is a posting that
    /// sums to zero across accounts that already existed. This is the one
    /// operation whose whole purpose is to raise the total, and it stays
    /// double entry for exactly that reason: the float rises because a mirror
    /// falls, and the mirror is what a bank statement is read against.
    /// </para>
    /// <para>
    /// <paramref name="by"/> comes from the token, never the body. An author
    /// field in the request would be a field somebody can fill in with a
    /// colleague's name.
    /// </para>
    /// </remarks>
    public async Task<CreditReceiptDto> CreditAsync(
        string by,
        CreditRequest request,
        string idempotencyKey,
        string? approvedBy = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(by);
        ArgumentException.ThrowIfNullOrWhiteSpace(idempotencyKey);

        var (destination, amount, source, reason) = Validate(request);
        var currency = amount.Currency;

        var into = destination == "float"
            ? AccountRef.CashFloat(currency)
            : AccountRef.SettlementFund(currency);
        var at = _clock.GetUtcNow();

        var receipt = await _ledger.PostAsync(
            new PostingRequest(
                Kind: "funding",
                Legs:
                [
                    new PostingLeg(into, amount),
                    // Negative by definition: the mirror is what IslaPay holds
                    // outside, and this money has just stopped being there.
                    new PostingLeg(AccountRef.External(source, currency), -amount),
                ],
                // Scoped, because the ledger's key is unique across the whole
                // book: a bare operator-chosen key could collide with one a
                // client picked, and the loser would be told it succeeded while
                // nothing moved.
                IdempotencyKey: $"treasury:credit:{idempotencyKey}",
                Metadata: new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["source"] = source,
                    ["reason"] = reason,
                    ["by"] = by,
                    ["destination"] = destination,
                    ["at"] = at.ToString("O", CultureInfo.InvariantCulture),
                    // Who agreed to it, when it went through a second person.
                    ["approved_by"] = approvedBy ?? "",
                }),
            cancellationToken).ConfigureAwait(false);

        var after = await _ledger.BalanceOfAsync(into, cancellationToken).ConfigureAwait(false);

        return new CreditReceiptDto(
            receipt.PostingId, destination, amount, after, source, reason, by, at, receipt.Written);
    }

    /// <summary>
    /// A credit as it will be posted, or the reason it cannot be.
    /// </summary>
    /// <remarks>
    /// Run when a credit is proposed, so the approver is never shown something
    /// that could not happen, and again when it is approved, because a
    /// currency can be switched off in between.
    /// </remarks>
    internal (string Destination, Money Amount, string Source, string Reason) Validate(CreditRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var destination = (request.Destination ?? string.Empty).Trim().ToLowerInvariant();
        if (!CreditableDestinations.Contains(destination, StringComparer.Ordinal))
        {
            throw new TreasuryException(
                TreasuryErrors.UnknownDestination, StatusCodes.Status422UnprocessableEntity,
                $"money can be credited into {string.Join(" or ", CreditableDestinations)}, "
                + $"not '{destination}'.");
        }

        if (!request.Amount.IsPositive)
        {
            throw new TreasuryException(
                TreasuryErrors.InvalidCredit, StatusCodes.Status422UnprocessableEntity,
                "a credit moves money in; to take money out, post its reverse.");
        }

        var source = (request.Source ?? string.Empty).Trim().ToLowerInvariant();
        if (!MirrorName().IsMatch(source))
        {
            throw new TreasuryException(
                TreasuryErrors.InvalidCredit, StatusCodes.Status422UnprocessableEntity,
                "a credit names where the money came from, as a mirror account "
                + "such as 'bank:bandec' or 'capital'.");
        }

        var reason = (request.Reason ?? string.Empty).Trim();
        if (reason.Length < 4)
        {
            throw new TreasuryException(
                TreasuryErrors.InvalidCredit, StatusCodes.Status422UnprocessableEntity,
                "a credit carries a reason: an unexplained one is indistinguishable "
                + "from a mistake.");
        }

        // Require, not Describe: money may not be put into a currency that has
        // been switched off, even though one that was switched off after the
        // fact can still be reported on.
        var currency = _catalog.Require(request.Amount.Currency.Code);
        return (destination, Money.FromMinorUnits(request.Amount.MinorUnits, currency), source, reason);
    }

    /// <summary>The chart of accounts' own name for a platform account.</summary>
    private static string NameOf(AccountOwner owner) => owner switch
    {
        AccountOwner.Fees => "fees",
        AccountOwner.SettlementFund => "settlement_fund",
        AccountOwner.Escrow => "escrow",
        AccountOwner.CashFloat => "float",
        AccountOwner.External => "external",
        _ => throw new TreasuryException(
            TreasuryErrors.UnknownAccount, StatusCodes.Status500InternalServerError,
            $"the ledger returned a {owner} account, which is somebody's money "
            + "and not the house's."),
    };

    /// <summary>The account a console route names, or a refusal.</summary>
    /// <remarks>
    /// User and merchant accounts are unreachable from here on purpose. This
    /// module is read by people looking at the platform's own position; a
    /// route that also served a customer's history would be a way to read
    /// anybody's transactions with one role and no audit of whose.
    /// </remarks>
    private static AccountRef AccountFor(string owner, Currency currency)
    {
        var name = (owner ?? string.Empty).Trim().ToLowerInvariant();

        if (name.StartsWith("external:", StringComparison.Ordinal))
        {
            var mirror = name["external:".Length..];
            return MirrorName().IsMatch(mirror)
                ? AccountRef.External(mirror, currency)
                : throw new TreasuryException(
                    TreasuryErrors.UnknownAccount, StatusCodes.Status404NotFound,
                    $"'{mirror}' is not the name of a mirror account.");
        }

        return name switch
        {
            "fees" => AccountRef.Fees(currency),
            "settlement_fund" => AccountRef.SettlementFund(currency),
            "escrow" => AccountRef.Escrow(currency),
            "float" => AccountRef.CashFloat(currency),
            _ => throw new TreasuryException(
                TreasuryErrors.UnknownAccount, StatusCodes.Status404NotFound,
                $"'{name}' is not one of the house's accounts."),
        };
    }

    /// <summary>
    /// What a mirror may be called.
    /// </summary>
    /// <remarks>
    /// Constrained because the name becomes part of an account's identifier,
    /// and an identifier that can contain anything is one where
    /// <c>bank:bandec</c> and <c>Bank: BANDEC </c> are two accounts holding
    /// half the money each.
    /// </remarks>
    [GeneratedRegex("^[a-z0-9][a-z0-9_.:-]{0,63}$", RegexOptions.CultureInvariant)]
    private static partial Regex MirrorName();
}
