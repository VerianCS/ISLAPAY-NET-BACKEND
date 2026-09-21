using IslaPay.Ledger.Contracts;
using IslaPay.P2P.Contracts;
using IslaPay.Platform;
using Microsoft.EntityFrameworkCore;

namespace IslaPay.P2P.Tests;

/// <summary>
/// Trading, against the real schema and a ledger that checks its own legs.
/// </summary>
/// <remarks>
/// The fake refuses a posting whose legs do not sum to zero per currency, so a
/// wrong set of legs fails here rather than only against a real database. That
/// matters more in this module than in any other: every trade posts across two
/// currencies at once, and the cross-currency arithmetic is the part nobody
/// would notice being wrong.
/// </remarks>
[Collection(P2PDefinition.Name)]
public sealed class TradeLifecycleTests : IAsyncLifetime
{
    private const string Rail = "cup_transfermovil";

    private readonly P2PFixture _fixture;

    public TradeLifecycleTests(P2PFixture fixture) => _fixture = fixture;

    public Task InitializeAsync() => _fixture.ResetAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    private static Money EIsla(string amount) => Money.Parse(amount, Currency.EIsla);

    private static Money Cup(string amount) => Money.Parse(amount, Currency.Cup);

    private sealed record World(
        P2PService Service, FakeLedger Ledger, FakeDirectory Directory,
        string User, FakeClock Clock, P2POptions Options);

    /// <summary>
    /// A market with a rate on both sides and a fund that can cover things.
    /// </summary>
    private async Task<World> SetUpAsync(
        string sellRate = "120", string buyRate = "125",
        string fundCup = "1000000.00", string fundEIsla = "10000.00",
        P2POptions? options = null, bool verified = true)
    {
        Skip.IfNot(_fixture.Available, "Postgres is not reachable.");

        var directory = new FakeDirectory();
        var user = $"user-{Guid.NewGuid():N}";
        directory.Add(user, "Ana", phoneVerified: verified);

        var ledger = new FakeLedger();
        ledger.Fund(AccountRef.SettlementFund(Currency.Cup), Cup(fundCup));
        ledger.Fund(AccountRef.SettlementFund(Currency.EIsla), EIsla(fundEIsla));

        var clock = new FakeClock(DateTimeOffset.Parse("2026-02-01T10:00:00Z", null));
        options ??= new P2POptions();
        var service = _fixture.Service(ledger, directory, options, clock);

        await service.SetRateAsync("op", new P2PRateUpdate(Rail, P2PSide.Sell, "EISLA", sellRate));
        await service.SetRateAsync("op", new P2PRateUpdate(Rail, P2PSide.Buy, "EISLA", buyRate));
        await service.SetAvailabilityAsync(Rail, available: true);

        return new World(service, ledger, directory, user, clock, options);
    }

    // ------------------------------------------------------------------ sell

    [SkippableFact]
    public async Task A_sell_takes_the_wallet_money_and_owes_the_local_money()
    {
        var w = await SetUpAsync();

        var trade = await w.Service.OpenAsync(
            w.User, new P2PTradeRequest(P2PSide.Sell, EIsla("100.00"), Rail));

        Assert.Equal(P2PTradeStatuses.AwaitingPayout, trade.Status);
        Assert.Equal(EIsla("1.00"), trade.Fee);
        // 99 converted, not 100: the fee is taken before the exchange.
        Assert.Equal(Cup("11880.00"), trade.Local);

        Assert.Equal(-10000, w.Ledger.Balance(AccountRef.User(w.User, Currency.EIsla)));
        Assert.Equal(9900, w.Ledger.Balance(AccountRef.SettlementFund(Currency.EIsla)) - 1000000);
        Assert.Equal(100, w.Ledger.Balance(AccountRef.Fees(Currency.EIsla)));

        // The local side: committed out of the fund and owed through escrow.
        Assert.Equal(1188000, w.Ledger.Balance(AccountRef.Escrow(Currency.Cup)));
        Assert.Equal(
            100000000 - 1188000,
            w.Ledger.Balance(AccountRef.SettlementFund(Currency.Cup)));
    }

    [SkippableFact]
    public async Task Confirming_a_payout_empties_the_escrow_through_the_rail()
    {
        var w = await SetUpAsync();
        var trade = await w.Service.OpenAsync(
            w.User, new P2PTradeRequest(P2PSide.Sell, EIsla("100.00"), Rail));

        var paid = await w.Service.ConfirmPayoutAsync("op", Guid.Parse(trade.Id), "TM-99887");

        Assert.Equal(P2PTradeStatuses.Completed, paid.Status);
        Assert.Equal(0, w.Ledger.Balance(AccountRef.Escrow(Currency.Cup)));

        // Positive on the mirror means sent out, the same convention a deposit
        // uses in reverse. It is what the operator reconciles against.
        Assert.Equal(
            1188000,
            w.Ledger.Balance(AccountRef.External(Rail, Currency.Cup)));
    }

    [SkippableFact]
    public async Task A_failed_payout_gives_back_the_fee_as_well()
    {
        var w = await SetUpAsync();
        var trade = await w.Service.OpenAsync(
            w.User, new P2PTradeRequest(P2PSide.Sell, EIsla("100.00"), Rail));

        var refunded = await w.Service.FailPayoutAsync(
            "op", Guid.Parse(trade.Id), "El número de teléfono no existe.");

        Assert.Equal(P2PTradeStatuses.Refunded, refunded.Status);
        Assert.Equal("El número de teléfono no existe.", refunded.FailureReason);

        // Whole again: IslaPay did not provide the service, so it does not
        // keep the charge.
        Assert.Equal(0, w.Ledger.Balance(AccountRef.User(w.User, Currency.EIsla)));
        Assert.Equal(0, w.Ledger.Balance(AccountRef.Fees(Currency.EIsla)));
        Assert.Equal(0, w.Ledger.Balance(AccountRef.Escrow(Currency.Cup)));
        Assert.Equal(100000000, w.Ledger.Balance(AccountRef.SettlementFund(Currency.Cup)));
    }

    [SkippableFact]
    public async Task A_seller_who_cannot_afford_it_leaves_no_trade_behind()
    {
        var w = await SetUpAsync();
        w.Ledger.RefuseWith = EIsla("4.00");

        var refused = await Assert.ThrowsAsync<P2PException>(
            () => w.Service.OpenAsync(w.User, new P2PTradeRequest(P2PSide.Sell, EIsla("100.00"), Rail)));

        Assert.Equal(P2PErrors.InsufficientFunds, refused.Code);
        Assert.Equal("EISLA", refused.Facts["currency"]);

        await using var db = _fixture.Context();
        Assert.False(await db.Trades.AnyAsync(t => t.UserId == w.User));
    }

    [SkippableFact]
    public async Task The_fund_refuses_a_payout_it_cannot_make()
    {
        // Ten CUP in the fund against a trade worth nearly twelve thousand.
        var w = await SetUpAsync(fundCup: "10.00");

        var refused = await Assert.ThrowsAsync<P2PException>(
            () => w.Service.OpenAsync(w.User, new P2PTradeRequest(P2PSide.Sell, EIsla("100.00"), Rail)));

        // The settlement fund is a platform account and may go negative, so
        // the ledger would have recorded this happily. Nothing else stops it.
        Assert.Equal(P2PErrors.FundUnavailable, refused.Code);
        Assert.Empty(w.Ledger.Posted);
    }

    [SkippableFact]
    public async Task A_quote_that_cannot_be_filled_is_still_a_successful_answer()
    {
        var w = await SetUpAsync(fundCup: "10.00");

        var quote = await w.Service.QuoteAsync(
            new P2PQuoteRequest(P2PSide.Sell, EIsla("100.00"), Rail));

        // Not an error: the client shows the number and explains why it cannot
        // be traded. See D2 — the fund's balance no longer travels.
        Assert.False(quote.Executable);
        Assert.Equal(P2PErrors.FundUnavailable, quote.Reason);
        Assert.Equal(Cup("11880.00"), quote.Local);
    }

    [SkippableFact]
    public async Task A_rate_that_moved_since_the_quote_refuses_the_trade()
    {
        var w = await SetUpAsync();
        var quote = await w.Service.QuoteAsync(
            new P2PQuoteRequest(P2PSide.Sell, EIsla("100.00"), Rail));

        w.Clock.Advance(TimeSpan.FromMinutes(1));
        await w.Service.SetRateAsync("op", new P2PRateUpdate(Rail, P2PSide.Sell, "EISLA", "90"));

        var refused = await Assert.ThrowsAsync<P2PException>(
            () => w.Service.OpenAsync(
                w.User,
                new P2PTradeRequest(P2PSide.Sell, EIsla("100.00"), Rail, QuotedRate: quote.Rate)));

        // Filling at a price the user never saw is worse than asking again.
        Assert.Equal(P2PErrors.QuoteExpired, refused.Code);
        Assert.Empty(w.Ledger.Posted);
    }

    [SkippableFact]
    public async Task A_seller_can_call_it_off_and_gets_everything_back()
    {
        var w = await SetUpAsync();
        var trade = await w.Service.OpenAsync(
            w.User, new P2PTradeRequest(P2PSide.Sell, EIsla("50.00"), Rail));

        var cancelled = await w.Service.CancelAsync(w.User, Guid.Parse(trade.Id));

        Assert.Equal(P2PTradeStatuses.Refunded, cancelled.Status);
        Assert.Equal(0, w.Ledger.Balance(AccountRef.User(w.User, Currency.EIsla)));
    }

    // ------------------------------------------------------------------- buy

    [SkippableFact]
    public async Task Opening_a_buy_moves_nothing_at_all()
    {
        var w = await SetUpAsync();

        var trade = await w.Service.OpenAsync(
            w.User, new P2PTradeRequest(P2PSide.Buy, EIsla("100.00"), Rail));

        Assert.Equal(P2PTradeStatuses.AwaitingPayment, trade.Status);
        // The gross converts, not the net: the fee comes out of the wallet
        // side afterwards, so the user sends the full counter-value.
        Assert.Equal(Cup("12500.00"), trade.Local);

        // Nothing is credited before it exists.
        Assert.Empty(w.Ledger.Posted);
        Assert.NotEmpty(trade.Reference);
    }

    [SkippableFact]
    public async Task Confirming_a_receipt_credits_the_buyer_the_net()
    {
        var w = await SetUpAsync();
        var trade = await w.Service.OpenAsync(
            w.User, new P2PTradeRequest(P2PSide.Buy, EIsla("100.00"), Rail));

        var credited = await w.Service.ConfirmReceiptAsync("op", Guid.Parse(trade.Id), "TM-12345");

        Assert.Equal(P2PTradeStatuses.Completed, credited.Status);
        Assert.Equal(9900, w.Ledger.Balance(AccountRef.User(w.User, Currency.EIsla)));
        Assert.Equal(100, w.Ledger.Balance(AccountRef.Fees(Currency.EIsla)));

        // Negative on the mirror means received from outside.
        Assert.Equal(-1250000, w.Ledger.Balance(AccountRef.External(Rail, Currency.Cup)));
        Assert.Equal(100000000 + 1250000, w.Ledger.Balance(AccountRef.SettlementFund(Currency.Cup)));
    }

    [SkippableFact]
    public async Task An_expired_buy_can_still_be_honoured()
    {
        var w = await SetUpAsync();
        var trade = await w.Service.OpenAsync(
            w.User, new P2PTradeRequest(P2PSide.Buy, EIsla("20.00"), Rail));

        w.Clock.Advance(w.Options.PaymentWindow + TimeSpan.FromMinutes(1));
        await w.Service.RepairAsync();

        Assert.Equal(
            P2PTradeStatuses.Expired,
            (await w.Service.TradeAsync(w.User, Guid.Parse(trade.Id))).Status);

        // The buyer transferred at the last minute and the operator looked a
        // few minutes later. Refusing would leave IslaPay holding money it has
        // no way to account for.
        var credited = await w.Service.ConfirmReceiptAsync("op", Guid.Parse(trade.Id), "TM-77");

        Assert.Equal(P2PTradeStatuses.Completed, credited.Status);
        Assert.Equal(1980, w.Ledger.Balance(AccountRef.User(w.User, Currency.EIsla)));
    }

    [SkippableFact]
    public async Task An_expired_buy_that_nobody_paid_costs_nothing()
    {
        var w = await SetUpAsync();
        await w.Service.OpenAsync(w.User, new P2PTradeRequest(P2PSide.Buy, EIsla("20.00"), Rail));

        w.Clock.Advance(w.Options.PaymentWindow + TimeSpan.FromMinutes(1));
        Assert.Equal(1, await w.Service.RepairAsync());

        Assert.Empty(w.Ledger.Posted);
    }

    // ------------------------------------------------------------ both sides

    [SkippableFact]
    public async Task Confirming_twice_settles_once()
    {
        var w = await SetUpAsync();
        var trade = await w.Service.OpenAsync(
            w.User, new P2PTradeRequest(P2PSide.Sell, EIsla("100.00"), Rail));

        await w.Service.ConfirmPayoutAsync("op", Guid.Parse(trade.Id), "TM-1");
        var again = await w.Service.ConfirmPayoutAsync("op", Guid.Parse(trade.Id), "TM-1");

        Assert.Equal(P2PTradeStatuses.Completed, again.Status);
        Assert.Single(
            w.Ledger.Posted,
            p => p.IdempotencyKey == P2PService.SettleKey(Guid.Parse(trade.Id)));
    }

    [SkippableFact]
    public async Task A_payout_cannot_be_confirmed_on_a_buy()
    {
        var w = await SetUpAsync();
        var trade = await w.Service.OpenAsync(
            w.User, new P2PTradeRequest(P2PSide.Buy, EIsla("20.00"), Rail));

        var refused = await Assert.ThrowsAsync<P2PException>(
            () => w.Service.ConfirmPayoutAsync("op", Guid.Parse(trade.Id), "TM-1"));

        Assert.Equal(P2PErrors.WrongSide, refused.Code);
    }

    [SkippableFact]
    public async Task A_settled_trade_cannot_be_settled_the_other_way()
    {
        var w = await SetUpAsync();
        var trade = await w.Service.OpenAsync(
            w.User, new P2PTradeRequest(P2PSide.Sell, EIsla("100.00"), Rail));

        await w.Service.ConfirmPayoutAsync("op", Guid.Parse(trade.Id), "TM-1");

        var refused = await Assert.ThrowsAsync<P2PException>(
            () => w.Service.FailPayoutAsync("op", Guid.Parse(trade.Id), "cambié de idea"));

        Assert.Equal(P2PErrors.TradeNotOpen, refused.Code);
        // And the fund did not pay twice, which is what the shared key buys.
        Assert.Single(
            w.Ledger.Posted,
            p => p.IdempotencyKey == P2PService.SettleKey(Guid.Parse(trade.Id)));
    }

    [SkippableFact]
    public async Task An_unverified_phone_cannot_trade()
    {
        var w = await SetUpAsync(verified: false);

        var refused = await Assert.ThrowsAsync<P2PException>(
            () => w.Service.OpenAsync(w.User, new P2PTradeRequest(P2PSide.Sell, EIsla("50.00"), Rail)));

        Assert.Equal(P2PErrors.PhoneNotVerified, refused.Code);
    }

    [SkippableFact]
    public async Task A_rail_with_no_rate_is_not_available_however_its_switch_is_set()
    {
        Skip.IfNot(_fixture.Available, "Postgres is not reachable.");

        var directory = new FakeDirectory();
        var ledger = new FakeLedger();
        var service = _fixture.Service(ledger, directory);

        // Switched on, but nobody has published a price.
        await service.SetAvailabilityAsync(Rail, available: true);

        var method = Assert.Single(await service.MethodsAsync());
        Assert.False(method.Available);
    }

    [SkippableFact]
    public async Task Amounts_outside_the_rails_limits_are_refused_with_the_figure()
    {
        var w = await SetUpAsync();

        var tooSmall = await Assert.ThrowsAsync<P2PException>(
            () => w.Service.OpenAsync(w.User, new P2PTradeRequest(P2PSide.Sell, EIsla("1.00"), Rail)));
        Assert.Equal(P2PErrors.BelowMinimum, tooSmall.Code);
        Assert.Equal("5.00", tooSmall.Facts["minimum"]);

        var tooBig = await Assert.ThrowsAsync<P2PException>(
            () => w.Service.OpenAsync(w.User, new P2PTradeRequest(P2PSide.Sell, EIsla("9000.00"), Rail)));
        Assert.Equal(P2PErrors.AboveMaximum, tooBig.Code);
        Assert.Equal("500.00", tooBig.Facts["maximum"]);
    }

    [SkippableFact]
    public async Task The_queue_shows_what_is_waiting_on_a_person()
    {
        var w = await SetUpAsync();
        var sell = await w.Service.OpenAsync(
            w.User, new P2PTradeRequest(P2PSide.Sell, EIsla("30.00"), Rail));

        w.Clock.Advance(TimeSpan.FromMinutes(20));

        var item = Assert.Single(await w.Service.QueueAsync(50));

        Assert.Equal(sell.Id, item.Id);
        Assert.Equal("Ana", item.UserName);
        Assert.Equal(sell.Reference, item.Reference);
        Assert.Equal(TimeSpan.FromMinutes(20), item.Waiting);
    }
}
