using System.Security.Claims;
using IslaPay.Ledger;
using IslaPay.Ledger.Contracts;
using IslaPay.Platform;
using IslaPay.Platform.AspNet;
using IslaPay.Platform.Data;
using IslaPay.Platform.Messaging;
using IslaPay.TestSupport;
using IslaPay.Treasury.Contracts;
using Microsoft.AspNetCore.Http;

namespace IslaPay.Treasury.Tests;

/// <summary>
/// A database of its own: these tests read absolute figures — the supply, the
/// headroom — and a book shared with the other treasury tests would make them
/// figures about whatever ran first.
/// </summary>
public sealed class IssuanceFixture : PostgresFixture
{
    protected override Task AfterCreateAsync() =>
        Migrator.ApplyAsync(Database,
        [
            new MigrationSet("ledger", typeof(LedgerModule).Assembly, "IslaPay.Ledger.Migrations."),
            new MigrationSet("messaging", typeof(Outbox).Assembly, "IslaPay.Platform.Messaging.Migrations."),
            new MigrationSet("platform", typeof(IdempotencyStore).Assembly, "IslaPay.Platform.AspNet.Migrations."),
            new MigrationSet("treasury", typeof(TreasuryModule).Assembly, "IslaPay.Treasury.Migrations."),
        ]);

    public PostgresLedger Ledger() => new(Database, new Outbox(Database), new TestCatalog());

    public TreasuryIssuance Issuance() => new(Ledger(), new TestCatalog(), TimeProvider.System);

    public TreasuryProposals Proposals()
    {
        var ledger = Ledger();
        return new(
            Database,
            new TreasuryService(ledger, new TestCatalog(), [], TimeProvider.System),
            new TreasuryIssuance(ledger, new TestCatalog(), TimeProvider.System),
            new TestCatalog(),
            new IslaPay.Platform.AspNet.Security.AuditLog(
                Database, TimeProvider.System,
                Microsoft.Extensions.Logging.Abstractions.NullLogger<IslaPay.Platform.AspNet.Security.AuditLog>.Instance),
            TimeProvider.System);
    }
}

[CollectionDefinition(Name)]
public sealed class IssuanceDefinition : ICollectionFixture<IssuanceFixture>
{
    public const string Name = "issuance";
}

/// <summary>
/// E-ISLA's supply, what backs it, and the only two ways to change it.
/// </summary>
/// <remarks>
/// In the order a real book would live it: E-ISLA that predates the issuer,
/// moved onto it; a mint refused for want of reserves; reserves arriving; the
/// mint approved; part of it burned back.
/// </remarks>
[Collection(IssuanceDefinition.Name)]
[Trait("Category", "Integration")]
public class IssuanceTests
{
    private readonly IssuanceFixture _postgres;

    public IssuanceTests(IssuanceFixture postgres) => _postgres = postgres;

    private static Money EIsla(string amount) => Money.Parse(amount, TestCurrencies.EIsla);

    [SkippableFact]
    public async Task The_issuer_carries_the_supply_and_only_reserves_let_it_grow()
    {
        Skip.IfNot(_postgres.Available, "No Postgres reachable.");
        var ledger = _postgres.Ledger();
        var issuance = _postgres.Issuance();
        var proposals = _postgres.Proposals();

        // 1. Before the issuer: 300 E-ISLA paid out of the float to a customer,
        //    the way funding worked until now. The float is 300 short.
        await ledger.PostAsync(new PostingRequest(
            "settlement",
            [
                new PostingLeg(AccountRef.User("ana", TestCurrencies.EIsla), EIsla("300.00")),
                new PostingLeg(AccountRef.CashFloat(TestCurrencies.EIsla), EIsla("-300.00")),
            ],
            IdempotencyKey: "legacy-funding"));

        var before = await issuance.ReportAsync();
        Assert.Equal(0, before.Outstanding.MinorUnits);
        Assert.Single(before.Strays);

        // 2. Genesis moves that onto the issuer, once.
        Assert.Equal(1, await issuance.GenesisAsync());
        Assert.Equal(0, await issuance.GenesisAsync());

        var after = await issuance.ReportAsync();
        Assert.Equal(EIsla("300.00"), after.Outstanding);
        Assert.Empty(after.Strays);
        Assert.Equal(0, (await ledger.BalanceOfAsync(AccountRef.CashFloat(TestCurrencies.EIsla))).MinorUnits);

        // Nothing backs those 300 yet, so a mint is refused even to propose.
        Assert.False(after.Backed);
        var refused = await Assert.ThrowsAsync<TreasuryException>(() => proposals.ProposeMintAsync(
            Person("ana"), new IssuanceRequest(EIsla("100.00"), "settlement_fund", "Existencias"), Key()));
        Assert.Equal(TreasuryErrors.ReserveInsufficient, refused.Code);

        // 3. 1,000 USDT arrive in the float from the chain.
        await ledger.PostAsync(new PostingRequest(
            "funding",
            [
                new PostingLeg(AccountRef.CashFloat(TestCurrencies.Usdt), Money.Parse("1000", TestCurrencies.Usdt)),
                new PostingLeg(AccountRef.External("tron", TestCurrencies.Usdt), Money.Parse("-1000", TestCurrencies.Usdt)),
            ],
            IdempotencyKey: "usdt-in"));

        var backed = await issuance.ReportAsync();
        Assert.True(backed.Backed);
        Assert.Equal("700", backed.Headroom);

        // 4. A mint of 500, proposed by one person and approved by another.
        var mint = await proposals.ProposeMintAsync(
            Person("ana"), new IssuanceRequest(EIsla("500.00"), "settlement_fund", "Existencias"), Key());
        Assert.Equal("mint", mint.Kind);
        Assert.Equal("issuer", mint.Source);

        var own = await Assert.ThrowsAsync<TreasuryException>(() => proposals.ApproveAsync(Person("ana"), mint.Id, null));
        Assert.Equal(TreasuryErrors.OwnProposal, own.Code);

        await proposals.ApproveAsync(Person("beto"), mint.Id, null);
        Assert.Equal(EIsla("800.00"), (await issuance.ReportAsync()).Outstanding);
        Assert.Equal(EIsla("500.00"), await ledger.BalanceOfAsync(AccountRef.SettlementFund(TestCurrencies.EIsla)));

        // 5. A second mint past the headroom is proposed while it fits…
        var greedy = await proposals.ProposeMintAsync(
            Person("ana"), new IssuanceRequest(EIsla("200.00"), "settlement_fund", "Más existencias"), Key());
        // …and refused at approval once the reserves have moved on.
        await ledger.PostAsync(new PostingRequest(
            "funding",
            [
                new PostingLeg(AccountRef.CashFloat(TestCurrencies.Usdt), Money.Parse("-150", TestCurrencies.Usdt)),
                new PostingLeg(AccountRef.External("tron", TestCurrencies.Usdt), Money.Parse("150", TestCurrencies.Usdt)),
            ],
            IdempotencyKey: "usdt-out"));
        var late = await Assert.ThrowsAsync<TreasuryException>(() => proposals.ApproveAsync(Person("beto"), greedy.Id, null));
        Assert.Equal(TreasuryErrors.ReserveInsufficient, late.Code);

        // 6. Burn 100 back; a burn past what the fund holds is refused.
        var burn = await proposals.ProposeBurnAsync(
            Person("ana"), new IssuanceRequest(EIsla("100.00"), "settlement_fund", "Retirar sobrante"), Key());
        await proposals.ApproveAsync(Person("beto"), burn.Id, null);
        Assert.Equal(EIsla("700.00"), (await issuance.ReportAsync()).Outstanding);

        var tooMuch = await proposals.ProposeBurnAsync(
            Person("ana"), new IssuanceRequest(EIsla("1000.00"), "settlement_fund", "Demasiado"), Key());
        var over = await Assert.ThrowsAsync<TreasuryException>(() => proposals.ApproveAsync(Person("beto"), tooMuch.Id, null));
        Assert.Equal(TreasuryErrors.BurnExceedsBalance, over.Code);
    }

    [SkippableFact]
    public async Task E_isla_is_minted_not_credited_and_nothing_else_is_minted()
    {
        Skip.IfNot(_postgres.Available, "No Postgres reachable.");
        var proposals = _postgres.Proposals();

        var credit = await Assert.ThrowsAsync<TreasuryException>(() => proposals.ProposeCreditAsync(
            Person("ana"), new CreditRequest("float", EIsla("10.00"), "capital", "Aporte"), Key()));
        Assert.Equal(TreasuryErrors.NotCreditable, credit.Code);

        var usdt = await Assert.ThrowsAsync<TreasuryException>(() => proposals.ProposeMintAsync(
            Person("ana"),
            new IssuanceRequest(Money.Parse("10", TestCurrencies.Usdt), "float", "No se emite"), Key()));
        Assert.Equal(TreasuryErrors.NotIssuable, usdt.Code);

        var fees = await Assert.ThrowsAsync<TreasuryException>(() => proposals.ProposeBurnAsync(
            Person("ana"), new IssuanceRequest(EIsla("1.00"), "fees", "Mal sitio"), Key()));
        Assert.Equal(TreasuryErrors.UnknownDestination, fees.Code);
    }

    private static string Key() => Guid.NewGuid().ToString("N");

    private static DefaultHttpContext Person(string subject) => new()
    {
        User = new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", subject)], "test")),
    };
}
