using IslaPay.TestSupport;
using IslaPay.Ledger.Contracts;
using IslaPay.Marketplace.Contracts;
using IslaPay.Platform;
using Microsoft.EntityFrameworkCore;

namespace IslaPay.Marketplace.Tests;

/// <summary>
/// What happens when a process dies between the ledger's commit and this
/// module's.
/// </summary>
/// <remarks>
/// <para>
/// This is the part of the design that exists only because the ledger owns its
/// own transaction, and it is the part most likely to be quietly wrong: the
/// failures it handles do not happen in development and each one leaves money
/// somewhere a customer cannot see it.
/// </para>
/// <para>
/// <see cref="FakeLedger.FailAfterPosting"/> reproduces the crash exactly —
/// posting committed, caller dead — which no real ledger can be asked to do.
/// </para>
/// </remarks>
[Collection(MarketplaceDefinition.Name)]
public sealed class RepairTests : IAsyncLifetime
{
    private readonly MarketplaceFixture _fixture;

    public RepairTests(MarketplaceFixture fixture) => _fixture = fixture;

    public Task InitializeAsync() => _fixture.ResetAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    private sealed record World(
        MarketplaceService Service, FakeLedger Ledger, FakeDirectory Directory,
        string Seller, string Buyer, FakeClock Clock, MarketplaceOptions Options);

    private World SetUp()
    {
        Skip.IfNot(_fixture.Available, "Postgres is not reachable.");

        var directory = new FakeDirectory();
        var seller = $"seller-{Guid.NewGuid():N}";
        var buyer = $"buyer-{Guid.NewGuid():N}";
        directory.Add(seller, "Ana");
        directory.Add(buyer, "Beto");

        var ledger = new FakeLedger();
        var clock = new FakeClock(DateTimeOffset.Parse("2026-01-01T10:00:00Z", null));
        var options = new MarketplaceOptions();
        return new World(
            _fixture.Service(ledger, directory, options, clock),
            ledger, directory, seller, buyer, clock, options);
    }

    private static PublishListingRequest AnItem() => new(
        Title: "Teclado",
        Description: null,
        Category: "Electrónica",
        Condition: "Nuevo",
        Price: Money.Parse("50.00", TestCurrencies.EIsla));

    private static async Task<(Guid Listing, Guid Order, string Code)> AHeldOrderAsync(World w)
    {
        var listing = await w.Service.PublishAsync(w.Seller, AnItem());
        var order = await w.Service.PlaceOrderAsync(w.Buyer, Guid.Parse(listing.Id));
        return (Guid.Parse(listing.Id), Guid.Parse(order.Id), order.Code!);
    }

    [SkippableFact]
    public async Task A_hold_nobody_scanned_comes_back_on_its_own()
    {
        var w = SetUp();
        var (listing, order, _) = await AHeldOrderAsync(w);

        w.Clock.Advance(w.Options.HoldDuration + TimeSpan.FromMinutes(1));
        var touched = await w.Service.RepairAsync();

        Assert.Equal(1, touched);
        Assert.Equal(0, w.Ledger.Balance(AccountRef.Escrow(TestCurrencies.EIsla)));
        Assert.Equal(0, w.Ledger.Balance(AccountRef.User(w.Buyer, TestCurrencies.EIsla)));

        var settled = await w.Service.OrderAsync(w.Buyer, order);
        Assert.Equal(OrderStatuses.Expired, settled.Status);

        var back = await w.Service.ListingAsync(listing);
        Assert.Equal(ListingStatuses.Active, back.Status);
    }

    [SkippableFact]
    public async Task A_hold_that_still_has_time_is_left_alone()
    {
        var w = SetUp();
        var (_, order, _) = await AHeldOrderAsync(w);

        w.Clock.Advance(w.Options.HoldDuration - TimeSpan.FromMinutes(1));

        Assert.Equal(0, await w.Service.RepairAsync());
        Assert.Equal(OrderStatuses.Held, (await w.Service.OrderAsync(w.Buyer, order)).Status);
    }

    [SkippableFact]
    public async Task A_hold_that_committed_before_the_crash_is_adopted_not_re_posted()
    {
        var w = SetUp();
        var listing = await w.Service.PublishAsync(w.Seller, AnItem());

        // The ledger takes the posting and the caller then dies, so the order
        // is left saying `pending` while the buyer's money really is in escrow.
        w.Ledger.FailAfterPosting = true;
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => w.Service.PlaceOrderAsync(w.Buyer, Guid.Parse(listing.Id)));

        Assert.Equal(5000, w.Ledger.Balance(AccountRef.Escrow(TestCurrencies.EIsla)));

        w.Clock.Advance(w.Options.InFlightGrace + TimeSpan.FromMinutes(1));
        Assert.Equal(1, await w.Service.RepairAsync());

        await using var db = _fixture.Context();
        var repaired = await db.Orders.AsNoTracking()
            .SingleAsync(o => o.ListingId == Guid.Parse(listing.Id));

        // Adopted: the money is where the order says it is, and the hold was
        // not posted a second time.
        Assert.Equal(OrderStatuses.Held, repaired.Status);
        Assert.NotNull(repaired.HoldPostingId);
        Assert.Single(w.Ledger.Posted);
        Assert.Equal(5000, w.Ledger.Balance(AccountRef.Escrow(TestCurrencies.EIsla)));
    }

    [SkippableFact]
    public async Task A_release_that_committed_before_the_crash_is_finished_not_refunded()
    {
        var w = SetUp();
        var (listing, order, code) = await AHeldOrderAsync(w);

        // The seller scanned, the payout committed, and the request died
        // before the order row was updated.
        w.Ledger.FailAfterPosting = true;
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => w.Service.RedeemAsync(w.Seller, code));

        Assert.Equal(4950, w.Ledger.Balance(AccountRef.User(w.Seller, TestCurrencies.EIsla)));

        // Now let the hold expire as well, so the sweeper has every reason to
        // think this needs refunding. It must not: the seller has been paid.
        w.Clock.Advance(w.Options.HoldDuration + TimeSpan.FromMinutes(1));
        await w.Service.RepairAsync();

        var settled = await w.Service.OrderAsync(w.Buyer, order);
        Assert.Equal(OrderStatuses.Released, settled.Status);

        Assert.Equal(0, w.Ledger.Balance(AccountRef.Escrow(TestCurrencies.EIsla)));
        Assert.Equal(-5000, w.Ledger.Balance(AccountRef.User(w.Buyer, TestCurrencies.EIsla)));
        Assert.Equal(ListingStatuses.Sold, (await w.Service.ListingAsync(listing)).Status);
    }

    [SkippableFact]
    public async Task A_release_that_never_committed_goes_back_to_held_so_the_seller_can_rescan()
    {
        var w = SetUp();
        var (_, order, code) = await AHeldOrderAsync(w);

        // Claimed for release, then the ledger was never reached at all.
        await using (var db = _fixture.Context())
        {
            await db.Database.ExecuteSqlAsync(
                $"UPDATE marketplace.orders SET status = 'releasing' WHERE id = {order}");
        }

        w.Clock.Advance(w.Options.InFlightGrace + TimeSpan.FromMinutes(1));
        Assert.Equal(1, await w.Service.RepairAsync());

        // Not paid out on the strength of a request that did not finish.
        Assert.Equal(0, w.Ledger.Balance(AccountRef.User(w.Seller, TestCurrencies.EIsla)));
        Assert.Equal(5000, w.Ledger.Balance(AccountRef.Escrow(TestCurrencies.EIsla)));

        var back = await w.Service.OrderAsync(w.Buyer, order);
        Assert.Equal(OrderStatuses.Held, back.Status);

        var released = await w.Service.RedeemAsync(w.Seller, code);
        Assert.Equal(OrderStatuses.Released, released.Status);
    }

    [SkippableFact]
    public async Task An_interrupted_refund_is_finished()
    {
        var w = SetUp();
        var (listing, order, _) = await AHeldOrderAsync(w);

        w.Ledger.FailAfterPosting = true;
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => w.Service.CancelAsync(w.Buyer, order));

        w.Clock.Advance(w.Options.InFlightGrace + TimeSpan.FromMinutes(1));
        Assert.Equal(1, await w.Service.RepairAsync());

        Assert.Equal(OrderStatuses.Cancelled, (await w.Service.OrderAsync(w.Buyer, order)).Status);
        Assert.Equal(0, w.Ledger.Balance(AccountRef.User(w.Buyer, TestCurrencies.EIsla)));
        Assert.Equal(ListingStatuses.Active, (await w.Service.ListingAsync(listing)).Status);
    }

    [SkippableFact]
    public async Task Escrow_never_pays_out_twice_however_the_order_ends()
    {
        var w = SetUp();
        var (_, order, code) = await AHeldOrderAsync(w);

        // Cancel and redeem racing each other, then the sweeper on top, with
        // the hold expired so every path wants to act. Escrow is a platform
        // account and may go negative, so a second payout would not bounce —
        // it would silently create money.
        await w.Service.CancelAsync(w.Buyer, order);

        await Assert.ThrowsAsync<MarketplaceException>(() => w.Service.RedeemAsync(w.Seller, code));

        w.Clock.Advance(w.Options.HoldDuration + TimeSpan.FromMinutes(1));
        await w.Service.RepairAsync();
        await w.Service.RepairAsync();

        Assert.Equal(0, w.Ledger.Balance(AccountRef.Escrow(TestCurrencies.EIsla)));
        Assert.Equal(0, w.Ledger.Balance(AccountRef.User(w.Buyer, TestCurrencies.EIsla)));
        Assert.Equal(0, w.Ledger.Balance(AccountRef.User(w.Seller, TestCurrencies.EIsla)));

        Assert.Single(
            w.Ledger.Posted, p => p.IdempotencyKey == MarketplaceService.SettleKey(order));
    }

    [SkippableFact]
    public async Task Sweeping_twice_settles_once()
    {
        var w = SetUp();
        var (_, order, _) = await AHeldOrderAsync(w);

        w.Clock.Advance(w.Options.HoldDuration + TimeSpan.FromMinutes(1));

        // Every instance runs a sweeper, so two of them racing is the normal
        // case rather than the exotic one.
        Assert.Equal(1, await w.Service.RepairAsync());
        Assert.Equal(0, await w.Service.RepairAsync());

        Assert.Equal(0, w.Ledger.Balance(AccountRef.User(w.Buyer, TestCurrencies.EIsla)));
        Assert.Single(
            w.Ledger.Posted, p => p.IdempotencyKey == MarketplaceService.SettleKey(order));
    }

    [SkippableFact]
    public async Task An_expired_hold_cannot_be_scanned()
    {
        var w = SetUp();
        var (_, _, code) = await AHeldOrderAsync(w);

        w.Clock.Advance(w.Options.HoldDuration + TimeSpan.FromMinutes(1));

        var refused = await Assert.ThrowsAsync<MarketplaceException>(
            () => w.Service.RedeemAsync(w.Seller, code));

        // Refused on the clock rather than on the sweeper having run: a seller
        // must not be paid because the sweep happened to be a minute late.
        Assert.Equal(MarketplaceErrors.OrderExpired, refused.Code);
        Assert.Equal(0, w.Ledger.Balance(AccountRef.User(w.Seller, TestCurrencies.EIsla)));
    }
}
