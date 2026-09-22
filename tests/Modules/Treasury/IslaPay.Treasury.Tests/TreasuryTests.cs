using IslaPay.Catalog.Contracts;
using IslaPay.Ledger;
using IslaPay.Ledger.Contracts;
using IslaPay.Platform;
using IslaPay.TestSupport;
using IslaPay.Treasury.Contracts;

namespace IslaPay.Treasury.Tests;

/// <summary>
/// The treasury against a real book.
/// </summary>
/// <remarks>
/// Every figure here is read back out of Postgres rather than out of a stub,
/// because the only question worth asking of this module is whether what the
/// console shows is what the database holds. A suite that asserted the
/// treasury's arithmetic against a fake ledger would pass on a day the two had
/// parted company.
/// </remarks>
[Collection(TreasuryDefinition.Name)]
[Trait("Category", "Integration")]
public class TreasuryTests
{
    private static readonly Currency EIsla = TestCurrencies.EIsla;
    private static readonly Currency Cup = TestCurrencies.Cup;

    private readonly TreasuryFixture _postgres;

    public TreasuryTests(TreasuryFixture postgres) => _postgres = postgres;

    private TreasuryService Treasury(params IslaPay.Platform.AspNet.IEscrowReporter[] reporters)
    {
        Skip.IfNot(_postgres.Available, "No Postgres reachable.");
        return _postgres.Treasury(reporters);
    }

    private PostgresLedger Ledger()
    {
        Skip.IfNot(_postgres.Available, "No Postgres reachable.");
        return _postgres.Ledger();
    }

    private static Money EIslas(string amount) => Money.Parse(amount, EIsla);

    private static string NewKey() => $"k{Guid.NewGuid():N}";

    /// <summary>Moves money into escrow without going through any module.</summary>
    /// <remarks>
    /// The float is the counterparty because it is allowed to go negative.
    /// What matters to these tests is only that escrow holds something the
    /// modules will then be asked to explain.
    /// </remarks>
    private static async Task FillEscrowAsync(PostgresLedger ledger, Money amount) =>
        await ledger.PostAsync(new PostingRequest(
            Kind: "settlement",
            Legs:
            [
                new PostingLeg(AccountRef.Escrow(amount.Currency), amount),
                new PostingLeg(AccountRef.CashFloat(amount.Currency), -amount),
            ],
            IdempotencyKey: NewKey())).ConfigureAwait(false);

    [SkippableFact]
    public async Task A_credit_raises_the_float_and_lowers_the_mirror_it_came_from()
    {
        var treasury = Treasury();
        var ledger = Ledger();

        var before = await ledger.BalanceOfAsync(AccountRef.CashFloat(EIsla));

        var receipt = await treasury.CreditAsync(
            by: "ana",
            new CreditRequest("float", EIslas("2500.00"), "bank:bandec", "Capital inicial"),
            NewKey());

        Assert.True(receipt.Applied);
        Assert.Equal(before.MinorUnits + 250000, receipt.BalanceAfter.MinorUnits);

        // Double entry, and the mirror is the half that makes it auditable: the
        // float rose because the bank account fell, and a statement can be read
        // against it.
        var mirror = await ledger.BalanceOfAsync(AccountRef.External("bank:bandec", EIsla));
        Assert.Equal(-250000, mirror.MinorUnits);
    }

    [SkippableFact]
    public async Task A_credit_records_who_did_it_and_why()
    {
        var treasury = Treasury();
        var ledger = Ledger();

        await treasury.CreditAsync(
            by: "ana",
            new CreditRequest("settlement_fund", EIslas("40.00"), "capital", "Fondeo de pruebas"),
            NewKey());

        var entries = await ledger.AccountEntriesAsync(AccountRef.SettlementFund(EIsla), 1);
        var entry = Assert.Single(entries.Items);

        // A credit nobody signed is indistinguishable from a mistake six months
        // later, which is the only moment anybody reads this.
        Assert.Equal("funding", entry.Kind);
        Assert.Equal("ana", entry.Metadata["by"]);
        Assert.Equal("Fondeo de pruebas", entry.Metadata["reason"]);
        Assert.Equal("capital", entry.Metadata["source"]);
    }

    [SkippableFact]
    public async Task The_same_credit_retried_does_not_fund_the_float_twice()
    {
        var treasury = Treasury();
        var ledger = Ledger();
        var key = NewKey();
        var request = new CreditRequest("float", EIslas("10.00"), "capital", "Un solo ingreso");

        var first = await treasury.CreditAsync("ana", request, key);
        var before = await ledger.BalanceOfAsync(AccountRef.CashFloat(EIsla));

        // A dropped connection, and the console pressing the button again.
        var second = await treasury.CreditAsync("ana", request, key);
        var after = await ledger.BalanceOfAsync(AccountRef.CashFloat(EIsla));

        Assert.Equal(first.PostingId, second.PostingId);
        Assert.False(second.Applied);
        Assert.Equal(before.MinorUnits, after.MinorUnits);
    }

    [SkippableTheory]
    [InlineData("escrow")]
    [InlineData("fees")]
    [InlineData("external")]
    public async Task Money_may_not_be_credited_anywhere_but_the_float_and_the_fund(string where)
    {
        var treasury = Treasury();

        var refusal = await Assert.ThrowsAsync<TreasuryException>(() => treasury.CreditAsync(
            "ana", new CreditRequest(where, EIslas("1.00"), "capital", "No debería entrar"), NewKey()));

        Assert.Equal(TreasuryErrors.UnknownDestination, refusal.Code);
    }

    [SkippableFact]
    public async Task A_credit_without_a_reason_is_refused()
    {
        var treasury = Treasury();

        var refusal = await Assert.ThrowsAsync<TreasuryException>(() => treasury.CreditAsync(
            "ana", new CreditRequest("float", EIslas("1.00"), "capital", "  "), NewKey()));

        Assert.Equal(TreasuryErrors.InvalidCredit, refusal.Code);
    }

    [SkippableFact]
    public async Task A_credit_in_a_currency_the_catalogue_will_not_allow_is_refused()
    {
        var treasury = Treasury();

        // The catalogue decides, not the treasury, and it has to: a currency
        // that is unlisted or switched off would otherwise stop customers
        // using it while the door money comes in by stayed open.
        await Assert.ThrowsAsync<UnknownCurrencyException>(() => treasury.CreditAsync(
            "ana",
            new CreditRequest("float", Money.Parse("1.00", Currency.Of("XXX", 2)), "capital", "No listada"),
            NewKey()));
    }

    [SkippableFact]
    public async Task The_balances_name_the_accounts_as_the_chart_of_accounts_does()
    {
        var treasury = Treasury();

        await treasury.CreditAsync(
            "ana", new CreditRequest("float", EIslas("5.00"), "bank:bpa", "Para el listado"), NewKey());

        var balances = await treasury.BalancesAsync();

        // 'float', not 'cash_float' or 'Float': the report, the support
        // conversation and the database all say the same word or none of them
        // can be searched for.
        Assert.Contains(balances.Accounts, a => a.Owner == "float" && a.Mirror is null);
        Assert.Contains(balances.Accounts, a => a.Owner == "external" && a.Mirror == "bank:bpa");
        Assert.DoesNotContain(balances.Accounts, a => a.Owner == "user");
    }

    [SkippableFact]
    public async Task Escrow_balances_when_the_modules_account_for_all_of_it()
    {
        var ledger = Ledger();
        await FillEscrowAsync(ledger, EIslas("300.00"));

        var escrow = await ledger.BalanceOfAsync(AccountRef.Escrow(EIsla));
        var treasury = Treasury(
            new StatedHoldings("marketplace").Holding(EIsla, escrow.MinorUnits - 10000),
            new StatedHoldings("p2p").Holding(EIsla, 10000));

        var report = await treasury.ReconciliationAsync();
        var row = Assert.Single(report.Currencies, c => c.Currency == "EISLA");

        Assert.True(report.Balanced);
        Assert.Equal(0, row.Difference.MinorUnits);
        Assert.Equal(2, row.Claims.Count);
    }

    [SkippableFact]
    public async Task Money_in_flight_widens_the_check_into_a_band_rather_than_an_alarm()
    {
        var ledger = Ledger();
        await FillEscrowAsync(ledger, EIslas("80.00"));

        var escrow = await ledger.BalanceOfAsync(AccountRef.Escrow(EIsla));

        // The module is certain of all but 80.00 and does not yet know whether
        // that last posting landed. Escrow says it did. Both readings are
        // consistent with a healthy system, so neither is an alarm.
        var treasury = Treasury(new StatedHoldings("marketplace")
            .Holding(EIsla, escrow.MinorUnits - 8000, inFlight: 8000));

        var report = await treasury.ReconciliationAsync();
        var row = Assert.Single(report.Currencies, c => c.Currency == "EISLA");

        Assert.True(row.Balanced);
        Assert.Equal(8000, row.InFlight.MinorUnits);
    }

    [SkippableFact]
    public async Task Escrow_holding_money_no_module_claims_is_reported_with_the_gap()
    {
        var ledger = Ledger();
        await FillEscrowAsync(ledger, EIslas("45.00"));

        var escrow = await ledger.BalanceOfAsync(AccountRef.Escrow(EIsla));
        var treasury = Treasury(
            new StatedHoldings("marketplace").Holding(EIsla, escrow.MinorUnits - 4500));

        var report = await treasury.ReconciliationAsync();
        var row = Assert.Single(report.Currencies, c => c.Currency == "EISLA");

        Assert.False(report.Balanced);
        // Positive: escrow is long. Somebody's hold was released in the module
        // and never released in the book, or money arrived without an order.
        Assert.Equal(4500, row.Difference.MinorUnits);
    }

    [SkippableFact]
    public async Task A_module_claiming_money_escrow_does_not_have_is_reported_too()
    {
        var ledger = Ledger();
        await FillEscrowAsync(ledger, EIslas("20.00"));

        var escrow = await ledger.BalanceOfAsync(AccountRef.Escrow(EIsla));
        var treasury = Treasury(
            new StatedHoldings("marketplace").Holding(EIsla, escrow.MinorUnits + 2000));

        var report = await treasury.ReconciliationAsync();
        var row = Assert.Single(report.Currencies, c => c.Currency == "EISLA");

        Assert.False(report.Balanced);
        // Negative: a buyer is owed a refund escrow cannot pay. The worse of
        // the two failures, and the one a single figure would have hidden.
        Assert.Equal(-2000, row.Difference.MinorUnits);
    }

    [SkippableFact]
    public async Task A_currency_only_a_module_mentions_is_still_checked()
    {
        // Nothing has ever been posted to escrow in pesos, so reading the
        // ledger alone would produce no row at all — and "no row" is how a
        // module claiming money that does not exist goes unnoticed.
        var treasury = Treasury(new StatedHoldings("p2p").Holding(Cup, 5000));

        var report = await treasury.ReconciliationAsync();
        var row = Assert.Single(report.Currencies, c => c.Currency == "CUP");

        Assert.False(row.Balanced);
        Assert.Equal(0, row.Ledger.MinorUnits);
        Assert.Equal(-5000, row.Difference.MinorUnits);
    }

    [SkippableFact]
    public async Task One_account_history_is_newest_first_and_stops_at_the_limit()
    {
        var treasury = Treasury();

        for (var i = 1; i <= 3; i++)
        {
            await treasury.CreditAsync(
                "ana",
                new CreditRequest("settlement_fund", EIslas($"{i}.00"), "capital", $"Tramo {i}"),
                NewKey());
        }

        var page = await treasury.EntriesAsync("settlement_fund", "EISLA", limit: 2, cursor: null);

        Assert.Equal(2, page.Items.Count);
        Assert.NotNull(page.NextCursor);
        Assert.True(page.Items[0].Id > page.Items[1].Id);
    }

    [SkippableFact]
    public async Task A_mirror_is_reachable_by_name_and_a_customer_is_not()
    {
        var treasury = Treasury();

        await treasury.CreditAsync(
            "ana", new CreditRequest("float", EIslas("7.00"), "bank:metro", "Historia"), NewKey());

        var mirror = await treasury.EntriesAsync("external:bank:metro", "EISLA", 10, null);
        Assert.NotEmpty(mirror.Items);

        // No route into somebody's money. This module is read by people whose
        // role was granted to look at the company's position, and a path that
        // also served a customer's history would be a way to read anybody's.
        var refusal = await Assert.ThrowsAsync<TreasuryException>(
            () => treasury.EntriesAsync("user:someone", "EISLA", 10, null));

        Assert.Equal(TreasuryErrors.UnknownAccount, refusal.Code);
    }
}
