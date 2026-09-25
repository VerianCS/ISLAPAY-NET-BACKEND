using IslaPay.TestSupport;
using IslaPay.Ledger.Contracts;
using IslaPay.P2P.Contracts;
using IslaPay.Platform;
using Microsoft.EntityFrameworkCore;

namespace IslaPay.P2P.Tests;

/// <summary>
/// What happens when a process dies between the ledger's commit and this
/// module's.
/// </summary>
/// <remarks>
/// The same window the marketplace has, with one more way through it: three
/// movements can be in flight rather than two, so an interrupted
/// <c>settling</c> has to be finished as the one it actually was. Finishing a
/// payout as a refund would pay the user twice; finishing a refund as a payout
/// would send money to a rail that already failed.
/// </remarks>
[Collection(P2PDefinition.Name)]
public sealed class TradeRepairTests : IAsyncLifetime
{
    private const string Rail = "cup";
    private const string Card = "9205 1299 0000 1234";

    private readonly P2PFixture _fixture;

    public TradeRepairTests(P2PFixture fixture) => _fixture = fixture;

    public Task InitializeAsync() => _fixture.ResetAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    private static Money EIsla(string amount) => Money.Parse(amount, TestCurrencies.EIsla);

    private static Money Cup(string amount) => Money.Parse(amount, TestCurrencies.Cup);

    private sealed record World(
        P2PService Service, FakeLedger Ledger, string User, FakeClock Clock, P2POptions Options);

    private async Task<World> SetUpAsync()
    {
        Skip.IfNot(_fixture.Available, "Postgres is not reachable.");

        var directory = new FakeDirectory();
        var user = $"user-{Guid.NewGuid():N}";
        directory.Add(user, "Ana");

        var ledger = new FakeLedger();
        ledger.Fund(AccountRef.SettlementFund(TestCurrencies.Cup), Cup("1000000.00"));
        ledger.Fund(AccountRef.SettlementFund(TestCurrencies.EIsla), EIsla("10000.00"));

        var clock = new FakeClock(DateTimeOffset.Parse("2026-02-01T10:00:00Z", null));
        var options = new P2POptions();
        var service = _fixture.Service(ledger, directory, options, clock);

        await service.SetRateAsync("op", new P2PRateUpdate(Rail, P2PSide.Sell, "EISLA", "120"));
        await service.SetRateAsync("op", new P2PRateUpdate(Rail, P2PSide.Buy, "EISLA", "125"));
        await service.SetAvailabilityAsync(Rail, available: true);

        return new World(service, ledger, user, clock, options);
    }

    private static Task<P2PTradeDto> SellAsync(World w, string amount = "100.00") =>
        w.Service.OpenAsync(w.User, new P2PTradeRequest(P2PSide.Sell, EIsla(amount), Rail, PayoutTo: Card));

    [SkippableFact]
    public async Task A_commit_that_landed_before_the_crash_is_adopted_not_re_posted()
    {
        var w = await SetUpAsync();

        // The ledger takes the posting and the caller then dies, so the trade
        // is left saying `pending` while the user's money really is committed.
        w.Ledger.FailAfterPosting = true;
        await Assert.ThrowsAsync<InvalidOperationException>(() => SellAsync(w));

        Assert.Equal(1188000, w.Ledger.Balance(AccountRef.Escrow(TestCurrencies.Cup)));

        w.Clock.Advance(w.Options.InFlightGrace + TimeSpan.FromMinutes(1));
        Assert.Equal(1, await w.Service.RepairAsync());

        await using var db = _fixture.Context();
        var repaired = await db.Trades.AsNoTracking().SingleAsync(t => t.UserId == w.User);

        Assert.Equal(P2PTradeStatuses.AwaitingPayout, repaired.Status);
        Assert.NotNull(repaired.CommitPostingId);
        Assert.Single(w.Ledger.Posted);
    }

    [SkippableFact]
    public async Task A_commit_that_never_landed_leaves_no_trade()
    {
        var w = await SetUpAsync();

        // A row written, then the ledger unreachable. Nothing moved.
        await using (var db = _fixture.Context())
        {
            await db.Database.ExecuteSqlAsync(
                $"""
                 INSERT INTO p2p.trades
                   (id, user_id, user_name, side, method_id, method_name,
                    wallet_currency, wallet_scale, amount_minor, fee_minor,
                    local_currency, local_scale, local_minor, rate, reference, status,
                    created_at, expires_at)
                 VALUES
                   ({Guid.NewGuid()}, {w.User}, 'Ana', 'sell', {Rail}, 'CUP Transfermóvil',
                    'EISLA', 2, 10000, 100, 'CUP', 2, 1188000, 120, 'ZZZZ-ZZZZ', 'pending',
                    {w.Clock.GetUtcNow()}, {w.Clock.GetUtcNow().AddHours(4)})
                 """);
        }

        w.Clock.Advance(w.Options.InFlightGrace + TimeSpan.FromMinutes(1));
        Assert.Equal(1, await w.Service.RepairAsync());

        await using var check = _fixture.Context();
        Assert.False(await check.Trades.AnyAsync(t => t.UserId == w.User));
        Assert.Empty(w.Ledger.Posted);
    }

    [SkippableFact]
    public async Task A_payout_that_landed_before_the_crash_is_finished_as_a_payout()
    {
        var w = await SetUpAsync();
        var trade = await SellAsync(w);

        w.Ledger.FailAfterPosting = true;
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => w.Service.ConfirmPayoutAsync("op", Guid.Parse(trade.Id), "TM-1"));

        Assert.Equal(1188000, w.Ledger.Balance(AccountRef.External(Rail, TestCurrencies.Cup)));

        w.Clock.Advance(w.Options.InFlightGrace + TimeSpan.FromMinutes(1));
        Assert.Equal(1, await w.Service.RepairAsync());

        var settled = await w.Service.TradeAsync(w.User, Guid.Parse(trade.Id));

        // Finished as what it was. Resolving it as a refund would have given
        // the user their E-ISLA back on top of the CUP they had just received.
        Assert.Equal(P2PTradeStatuses.Completed, settled.Status);
        Assert.Equal(-10000, w.Ledger.Balance(AccountRef.User(w.User, TestCurrencies.EIsla)));
    }

    [SkippableFact]
    public async Task A_refund_that_landed_before_the_crash_is_finished_as_a_refund()
    {
        var w = await SetUpAsync();
        var trade = await SellAsync(w);

        w.Ledger.FailAfterPosting = true;
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => w.Service.FailPayoutAsync("op", Guid.Parse(trade.Id), "sin saldo en la cuenta"));

        w.Clock.Advance(w.Options.InFlightGrace + TimeSpan.FromMinutes(1));
        Assert.Equal(1, await w.Service.RepairAsync());

        var settled = await w.Service.TradeAsync(w.User, Guid.Parse(trade.Id));

        Assert.Equal(P2PTradeStatuses.Refunded, settled.Status);
        Assert.Equal("sin saldo en la cuenta", settled.FailureReason);
        Assert.Equal(0, w.Ledger.Balance(AccountRef.User(w.User, TestCurrencies.EIsla)));
        Assert.Equal(0, w.Ledger.Balance(AccountRef.External(Rail, TestCurrencies.Cup)));
    }

    [SkippableFact]
    public async Task A_settlement_that_never_landed_goes_back_to_the_queue()
    {
        var w = await SetUpAsync();
        var trade = await SellAsync(w);

        // Claimed, then the ledger was never reached at all.
        await using (var db = _fixture.Context())
        {
            await db.Database.ExecuteSqlAsync(
                $"""
                 UPDATE p2p.trades SET status = 'settling', settle_intent = 'payout'
                 WHERE id = {Guid.Parse(trade.Id)}
                 """);
        }

        w.Clock.Advance(w.Options.InFlightGrace + TimeSpan.FromMinutes(1));
        Assert.Equal(1, await w.Service.RepairAsync());

        // Not paid out on the strength of a request that did not finish: back
        // in the queue for a person to decide again.
        Assert.Equal(0, w.Ledger.Balance(AccountRef.External(Rail, TestCurrencies.Cup)));
        Assert.Equal(1188000, w.Ledger.Balance(AccountRef.Escrow(TestCurrencies.Cup)));

        var back = await w.Service.TradeAsync(w.User, Guid.Parse(trade.Id));
        Assert.Equal(P2PTradeStatuses.AwaitingPayout, back.Status);

        var paid = await w.Service.ConfirmPayoutAsync("op", Guid.Parse(trade.Id), "TM-2");
        Assert.Equal(P2PTradeStatuses.Completed, paid.Status);
    }

    [SkippableFact]
    public async Task An_interrupted_credit_goes_back_to_awaiting_payment()
    {
        var w = await SetUpAsync();
        var trade = await w.Service.OpenAsync(
            w.User, new P2PTradeRequest(P2PSide.Buy, EIsla("40.00"), Rail));

        await using (var db = _fixture.Context())
        {
            await db.Database.ExecuteSqlAsync(
                $"""
                 UPDATE p2p.trades SET status = 'settling', settle_intent = 'credit'
                 WHERE id = {Guid.Parse(trade.Id)}
                 """);
        }

        w.Clock.Advance(w.Options.InFlightGrace + TimeSpan.FromMinutes(1));
        await w.Service.RepairAsync();

        // A buy reverts to its own waiting room, not a seller's. The schema
        // refuses the other one outright.
        var back = await w.Service.TradeAsync(w.User, Guid.Parse(trade.Id));
        Assert.Equal(P2PTradeStatuses.AwaitingPayment, back.Status);
        Assert.Equal(0, w.Ledger.Balance(AccountRef.User(w.User, TestCurrencies.EIsla)));
    }

    [SkippableFact]
    public async Task The_fund_never_pays_out_twice_however_the_trade_ends()
    {
        var w = await SetUpAsync();
        var trade = await SellAsync(w);

        // A cancel and a payout racing, then the sweeper on top.
        await w.Service.CancelAsync(w.User, Guid.Parse(trade.Id));

        await Assert.ThrowsAsync<P2PException>(
            () => w.Service.ConfirmPayoutAsync("op", Guid.Parse(trade.Id), "TM-1"));

        w.Clock.Advance(w.Options.InFlightGrace + TimeSpan.FromHours(1));
        await w.Service.RepairAsync();
        await w.Service.RepairAsync();

        Assert.Equal(0, w.Ledger.Balance(AccountRef.Escrow(TestCurrencies.Cup)));
        Assert.Equal(0, w.Ledger.Balance(AccountRef.External(Rail, TestCurrencies.Cup)));
        Assert.Equal(100000000, w.Ledger.Balance(AccountRef.SettlementFund(TestCurrencies.Cup)));
        Assert.Equal(0, w.Ledger.Balance(AccountRef.User(w.User, TestCurrencies.EIsla)));

        Assert.Single(
            w.Ledger.Posted, p => p.IdempotencyKey == P2PService.SettleKey(Guid.Parse(trade.Id)));
    }

    [SkippableFact]
    public async Task A_committed_sell_is_never_expired_by_a_timer()
    {
        var w = await SetUpAsync();
        var trade = await SellAsync(w);

        // Long past its payout target. A timer does not get to decide that
        // somebody's money goes back — only a person does.
        w.Clock.Advance(TimeSpan.FromDays(30));
        Assert.Equal(0, await w.Service.RepairAsync());

        var still = await w.Service.TradeAsync(w.User, Guid.Parse(trade.Id));
        Assert.Equal(P2PTradeStatuses.AwaitingPayout, still.Status);
        Assert.Equal(1188000, w.Ledger.Balance(AccountRef.Escrow(TestCurrencies.Cup)));
    }

    [SkippableFact]
    public async Task Sweeping_twice_settles_once()
    {
        var w = await SetUpAsync();
        await w.Service.OpenAsync(w.User, new P2PTradeRequest(P2PSide.Buy, EIsla("20.00"), Rail));

        w.Clock.Advance(w.Options.PaymentWindow + TimeSpan.FromMinutes(1));

        Assert.Equal(1, await w.Service.RepairAsync());
        Assert.Equal(0, await w.Service.RepairAsync());
    }
}
