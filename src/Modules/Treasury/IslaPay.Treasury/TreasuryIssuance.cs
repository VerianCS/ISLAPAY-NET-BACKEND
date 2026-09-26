using System.Globalization;
using IslaPay.Catalog.Contracts;
using IslaPay.Ledger.Contracts;
using IslaPay.Platform;
using IslaPay.Treasury.Contracts;
using Microsoft.AspNetCore.Http;

namespace IslaPay.Treasury;

/// <summary>
/// E-ISLA's one source, and the rule that keeps it honest.
/// </summary>
/// <remarks>
/// <para>
/// Every E-ISLA in existence came out of <see cref="AccountRef.Issuer"/>, so
/// the issuer's balance, negated, is the supply — read in one place rather
/// than summed over every wallet. Minting moves E-ISLA from the issuer into
/// the settlement fund or the float; burning moves it back. Nothing else
/// posts to the issuer.
/// </para>
/// <para>
/// The rule: what is outstanding may not exceed the stablecoins IslaPay holds
/// in its own name. A mint that would break it is refused when it is approved,
/// whoever approves it. What makes this a peg rather than a promise is that
/// the only other way E-ISLA reaches a customer — converting USDT or USDC — puts
/// the stablecoin into the same reserve the E-ISLA came out against.
/// </para>
/// <para>
/// Which currencies are issued and which back them is the catalogue's word
/// (<see cref="CurrencyKinds.Internal"/> and <see cref="CurrencyKinds.Stablecoin"/>),
/// each stablecoin counted at one dollar.
/// </para>
/// </remarks>
public sealed class TreasuryIssuance
{
    /// <summary>Where minted E-ISLA may land and burned E-ISLA be taken from.</summary>
    private static readonly string[] Accounts = ["settlement_fund", "float"];

    /// <summary>The house accounts whose stablecoins count as reserve. Not escrow: that is owed.</summary>
    private static readonly AccountOwner[] ReserveOwners =
        [AccountOwner.CashFloat, AccountOwner.SettlementFund, AccountOwner.Fees];

    private readonly ILedger _ledger;
    private readonly ICurrencyCatalog _catalog;
    private readonly TimeProvider _clock;

    public TreasuryIssuance(ILedger ledger, ICurrencyCatalog catalog, TimeProvider clock)
    {
        _ledger = ledger;
        _catalog = catalog;
        _clock = clock;
    }

    public async Task<IssuanceDto> ReportAsync(CancellationToken ct = default)
    {
        var house = await _ledger.HouseBalancesAsync(ct).ConfigureAwait(false);
        var issued = Issued();
        var reserveCodes = Reserve();

        var outstanding = issued.Sum(c => -BalanceIn(house, AccountOwner.Issuer, c));
        var reserves = reserveCodes
            .Select(c => Money.FromMinorUnits(
                ReserveOwners.Sum(owner => BalanceIn(house, owner, c)), c))
            .ToList();
        var total = reserves.Sum(r => r.ToDecimal());
        if (issued.Count == 0)
        {
            throw new TreasuryException(
                TreasuryErrors.NotIssuable, StatusCodes.Status500InternalServerError,
                "the catalogue lists no internal currency to report on.");
        }

        var primary = issued[0];

        var strays = house
            .Where(b => issued.Any(c => c.Code == b.Account.Currency.Code)
                && b.Account.Owner != AccountOwner.Issuer
                && b.Account.Owner != AccountOwner.User
                && b.Account.Owner != AccountOwner.Merchant
                && b.Balance.IsNegative)
            .Select(b => new TreasuryAccountDto(
                b.Account.Owner == AccountOwner.External ? "external" : Name(b.Account.Owner),
                b.Account.Owner == AccountOwner.External ? b.Account.Id : null,
                b.Balance, b.EntryCount))
            .ToList();

        var owed = Money.FromMinorUnits(outstanding, primary);
        return new IssuanceDto(
            _clock.GetUtcNow(),
            owed,
            reserves,
            Decimal(total),
            Decimal(Math.Max(0m, total - owed.ToDecimal())),
            owed.ToDecimal() <= total,
            strays);
    }

    /// <summary>The currency and account of a mint or burn, or why not.</summary>
    internal (string Account, Money Amount, string Reason) Validate(IssuanceRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var info = _catalog.Describe(request.Amount.Currency.Code);
        if (info is null || info.Kind != CurrencyKinds.Internal)
        {
            throw new TreasuryException(
                TreasuryErrors.NotIssuable, StatusCodes.Status422UnprocessableEntity,
                $"IslaPay issues its internal currency only; {request.Amount.Currency.Code} is not one.");
        }

        if (!request.Amount.IsPositive)
        {
            throw new TreasuryException(
                TreasuryErrors.InvalidCredit, StatusCodes.Status422UnprocessableEntity,
                "an amount to issue or retire is positive.");
        }

        var account = (request.Account ?? string.Empty).Trim().ToLowerInvariant();
        if (!Accounts.Contains(account, StringComparer.Ordinal))
        {
            throw new TreasuryException(
                TreasuryErrors.UnknownDestination, StatusCodes.Status422UnprocessableEntity,
                $"E-ISLA is issued into, and retired from, {string.Join(" or ", Accounts)}.");
        }

        var reason = (request.Reason ?? string.Empty).Trim();
        if (reason.Length < 4)
        {
            throw new TreasuryException(
                TreasuryErrors.InvalidCredit, StatusCodes.Status422UnprocessableEntity,
                "issuing or retiring money carries a reason.");
        }

        return (account, Money.FromMinorUnits(request.Amount.MinorUnits, info.Currency), reason);
    }

    /// <summary>Refuses a mint the reserves would not cover.</summary>
    internal async Task RequireCoveredAsync(Money more, CancellationToken ct)
    {
        var report = await ReportAsync(ct).ConfigureAwait(false);
        var headroom = decimal.Parse(report.Headroom, CultureInfo.InvariantCulture);
        if (more.ToDecimal() > headroom)
        {
            throw new TreasuryException(
                TreasuryErrors.ReserveInsufficient, StatusCodes.Status422UnprocessableEntity,
                $"the reserves cover {report.Headroom} more {more.Currency.Code}, not {more}.",
                new Dictionary<string, object>(StringComparer.Ordinal)
                {
                    ["headroom"] = report.Headroom,
                    ["currency"] = more.Currency.Code,
                });
        }
    }

    /// <summary>Issuer to account. Run by an approval; the key is the proposal's.</summary>
    internal async Task<PostingReceipt> MintAsync(
        TreasuryProposalDto proposal, string approvedBy, CancellationToken ct)
    {
        // Checked unless this exact mint already went through, in which case
        // the outstanding figure includes it and the ledger's key answers.
        if (await _ledger.FindPostingAsync($"proposal:{proposal.Id:N}", ct).ConfigureAwait(false) is null)
            await RequireCoveredAsync(proposal.Amount, ct).ConfigureAwait(false);
        return await _ledger.PostAsync(new PostingRequest(
            Kind: "issuance",
            Legs:
            [
                new PostingLeg(AccountRef.Issuer(proposal.Amount.Currency), -proposal.Amount),
                new PostingLeg(Account(proposal.Destination, proposal.Amount.Currency), proposal.Amount),
            ],
            IdempotencyKey: $"proposal:{proposal.Id:N}",
            Metadata: Metadata("mint", proposal, approvedBy)), ct).ConfigureAwait(false);
    }

    /// <summary>Account back to issuer. Refused past what the account holds.</summary>
    internal async Task<PostingReceipt> BurnAsync(
        TreasuryProposalDto proposal, string approvedBy, CancellationToken ct)
    {
        var from = Account(proposal.Source, proposal.Amount.Currency);
        var held = await _ledger.BalanceOfAsync(from, ct).ConfigureAwait(false);
        if (held.MinorUnits < proposal.Amount.MinorUnits)
        {
            // Unless this exact burn already went through: then the balance is
            // lower because of it, and the ledger's key answers the retry.
            if (await _ledger.FindPostingAsync($"proposal:{proposal.Id:N}", ct).ConfigureAwait(false) is null)
            {
                throw new TreasuryException(
                    TreasuryErrors.BurnExceedsBalance, StatusCodes.Status422UnprocessableEntity,
                    $"{proposal.Source} holds {held}, less than {proposal.Amount}.");
            }
        }

        return await _ledger.PostAsync(new PostingRequest(
            Kind: "issuance",
            Legs:
            [
                new PostingLeg(from, -proposal.Amount),
                new PostingLeg(AccountRef.Issuer(proposal.Amount.Currency), proposal.Amount),
            ],
            IdempotencyKey: $"proposal:{proposal.Id:N}",
            Metadata: Metadata("burn", proposal, approvedBy)), ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Puts the E-ISLA that existed before the issuer did onto the issuer.
    /// </summary>
    /// <remarks>
    /// Before the issuer, E-ISLA came into the books from a mirror account or
    /// the float, which went negative to pay for it. Those negatives are the
    /// supply of the time. One posting per currency moves each of them onto the
    /// issuer, so what is outstanding is read from the issuer from then on and
    /// nothing is lost or counted twice. Runs once: after it, the issuer has
    /// entries and this does nothing.
    /// </remarks>
    public async Task<int> GenesisAsync(CancellationToken ct = default)
    {
        var house = await _ledger.HouseBalancesAsync(ct).ConfigureAwait(false);
        var moved = 0;

        foreach (var currency in Issued())
        {
            var issuer = house.FirstOrDefault(b =>
                b.Account.Owner == AccountOwner.Issuer && b.Account.Currency.Code == currency.Code);
            if (issuer is { EntryCount: > 0 }) continue;

            var strays = house
                .Where(b => b.Account.Currency.Code == currency.Code
                    && b.Account.Owner is not (AccountOwner.Issuer or AccountOwner.User or AccountOwner.Merchant)
                    && b.Balance.IsNegative)
                .ToList();
            if (strays.Count == 0) continue;

            var total = strays.Sum(b => -b.Balance.MinorUnits);
            await _ledger.PostAsync(new PostingRequest(
                Kind: "issuance",
                Legs:
                [
                    .. strays.Select(b => new PostingLeg(b.Account, -b.Balance)),
                    new PostingLeg(AccountRef.Issuer(currency), Money.FromMinorUnits(-total, currency)),
                ],
                IdempotencyKey: $"treasury:issuer-genesis:{currency.Code}",
                Metadata: new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["kind"] = "genesis",
                    ["reason"] = "E-ISLA issued before the issuer existed, moved onto it",
                    ["at"] = _clock.GetUtcNow().ToString("O", CultureInfo.InvariantCulture),
                }), ct).ConfigureAwait(false);
            moved++;
        }

        return moved;
    }

    /// <summary>Whether IslaPay issues this currency, and so does not accept it as a credit.</summary>
    internal bool IsIssued(string code) =>
        string.Equals(_catalog.Describe(code)?.Kind, CurrencyKinds.Internal, StringComparison.Ordinal);

    private List<Currency> Issued() =>
        [.. _catalog.Currencies.Where(c => c.Kind == CurrencyKinds.Internal).Select(c => c.Currency)];

    private List<Currency> Reserve() =>
        [.. _catalog.Currencies.Where(c => c.Kind == CurrencyKinds.Stablecoin).Select(c => c.Currency)];

    private static long BalanceIn(IReadOnlyList<HouseBalance> house, AccountOwner owner, Currency currency) =>
        house.Where(b => b.Account.Owner == owner && b.Account.Currency.Code == currency.Code)
            .Sum(b => b.Balance.MinorUnits);

    private static AccountRef Account(string name, Currency currency) => name switch
    {
        "float" => AccountRef.CashFloat(currency),
        "settlement_fund" => AccountRef.SettlementFund(currency),
        _ => throw new TreasuryException(
            TreasuryErrors.UnknownDestination, StatusCodes.Status422UnprocessableEntity,
            $"'{name}' is not an account E-ISLA is issued into."),
    };

    private static string Name(AccountOwner owner) => owner switch
    {
        AccountOwner.Fees => "fees",
        AccountOwner.SettlementFund => "settlement_fund",
        AccountOwner.Escrow => "escrow",
        AccountOwner.CashFloat => "float",
        _ => owner.ToString().ToLowerInvariant(),
    };

    private static Dictionary<string, string> Metadata(string kind, TreasuryProposalDto proposal, string approvedBy) =>
        new(StringComparer.Ordinal)
        {
            ["kind"] = kind,
            ["reason"] = proposal.Reason,
            ["by"] = proposal.ProposedBy,
            ["approved_by"] = approvedBy,
            ["proposal"] = proposal.Id.ToString("D", CultureInfo.InvariantCulture),
        };

    private static string Decimal(decimal value) => value.ToString("0.######", CultureInfo.InvariantCulture);
}
