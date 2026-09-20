using IslaPay.Ledger.Contracts;
using IslaPay.Marketplace.Contracts;
using IslaPay.Platform;
using Microsoft.EntityFrameworkCore;

namespace IslaPay.Marketplace.Tests;

/// <summary>
/// The order state machine, against the real schema.
/// </summary>
/// <remarks>
/// The ledger is faked here — see <see cref="FakeLedger"/> — because what
/// these tests are about is what happens to the order row when a posting
/// succeeds, fails, or succeeds and is then lost. A real ledger cannot be
/// asked to die at the interesting moment. The money itself is checked against
/// the real one, end to end.
/// </remarks>
[Collection(MarketplaceDefinition.Name)]
public sealed class OrderLifecycleTests : IAsyncLifetime
{
    private readonly MarketplaceFixture _fixture;

    public OrderLifecycleTests(MarketplaceFixture fixture) => _fixture = fixture;

    public Task InitializeAsync() => _fixture.ResetAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    private static Money Usd(string amount) => Money.Parse(amount, Currency.Usd);

    private sealed record World(
        MarketplaceService Service, FakeLedger Ledger, FakeDirectory Directory,
        string Seller, string Buyer, FakeClock Clock);

    private async Task<World> SetUpAsync(MarketplaceOptions? options = null)
    {
        Skip.IfNot(_fixture.Available, "Postgres is not reachable.");

        var directory = new FakeDirectory();
        var seller = $"seller-{Guid.NewGuid():N}";
        var buyer = $"buyer-{Guid.NewGuid():N}";
        directory.Add(seller, "Ana");
        directory.Add(buyer, "Beto");

        var ledger = new FakeLedger();
        var clock = new FakeClock(DateTimeOffset.Parse("2026-01-01T10:00:00Z", null));
        var service = _fixture.Service(ledger, directory, options, clock);

        await Task.CompletedTask;
        return new World(service, ledger, directory, seller, buyer, clock);
    }

    private static PublishListingRequest AnItem(string price = "100.00") => new(
        Title: "Bicicleta",
        Description: "Poco uso",
        Category: "Deportes",
        Condition: "Como nuevo",
        Price: Money.Parse(price, Currency.Usd),
        Location: "Habana",
        Photos: []);

    [SkippableFact]
    public async Task Locking_a_price_moves_the_money_into_escrow_and_reserves_the_item()
    {
        var w = await SetUpAsync();
        var listing = await w.Service.PublishAsync(w.Seller, AnItem());

        var order = await w.Service.PlaceOrderAsync(w.Buyer, Guid.Parse(listing.Id));

        Assert.Equal(OrderStatuses.Held, order.Status);
        Assert.Equal(Usd("100.00"), order.Amount);
        Assert.Equal(Usd("1.00"), order.Fee);
        Assert.Equal(Usd("99.00"), order.SellerReceives);

        // The buyer is down the whole price and escrow holds it. Not a flag on
        // a row: the balance a buyer sees has to reflect the commitment, or
        // the same money can be promised to three sellers.
        Assert.Equal(-10000, w.Ledger.Balance(AccountRef.User(w.Buyer, Currency.Usd)));
        Assert.Equal(10000, w.Ledger.Balance(AccountRef.Escrow(Currency.Usd)));

        var after = await w.Service.ListingAsync(Guid.Parse(listing.Id));
        Assert.Equal(ListingStatuses.Reserved, after.Status);
    }

    [SkippableFact]
    public async Task The_code_goes_to_the_buyer_and_never_to_the_seller()
    {
        var w = await SetUpAsync();
        var listing = await w.Service.PublishAsync(w.Seller, AnItem());
        var placed = await w.Service.PlaceOrderAsync(w.Buyer, Guid.Parse(listing.Id));

        Assert.NotNull(placed.Code);

        var asBuyer = await w.Service.OrderAsync(w.Buyer, Guid.Parse(placed.Id));
        var asSeller = await w.Service.OrderAsync(w.Seller, Guid.Parse(placed.Id));

        Assert.Equal(placed.Code, asBuyer.Code);

        // The whole mechanism in one assertion. A seller who could read this
        // would be able to collect without handing anything over.
        Assert.Null(asSeller.Code);
    }

    [SkippableFact]
    public async Task Scanning_the_code_pays_the_seller_the_price_less_the_commission()
    {
        var w = await SetUpAsync();
        var listing = await w.Service.PublishAsync(w.Seller, AnItem());
        var placed = await w.Service.PlaceOrderAsync(w.Buyer, Guid.Parse(listing.Id));

        var released = await w.Service.RedeemAsync(w.Seller, placed.Code);

        Assert.Equal(OrderStatuses.Released, released.Status);
        Assert.Equal(0, w.Ledger.Balance(AccountRef.Escrow(Currency.Usd)));
        Assert.Equal(9900, w.Ledger.Balance(AccountRef.User(w.Seller, Currency.Usd)));
        Assert.Equal(100, w.Ledger.Balance(AccountRef.Fees(Currency.Usd)));

        var after = await w.Service.ListingAsync(Guid.Parse(listing.Id));
        Assert.Equal(ListingStatuses.Sold, after.Status);
    }

    [SkippableFact]
    public async Task A_code_is_worthless_to_anyone_but_the_seller_it_belongs_to()
    {
        var w = await SetUpAsync();
        var stranger = $"stranger-{Guid.NewGuid():N}";
        w.Directory.Add(stranger);

        var listing = await w.Service.PublishAsync(w.Seller, AnItem());
        var placed = await w.Service.PlaceOrderAsync(w.Buyer, Guid.Parse(listing.Id));

        var refused = await Assert.ThrowsAsync<MarketplaceException>(
            () => w.Service.RedeemAsync(stranger, placed.Code));

        Assert.Equal(MarketplaceErrors.CodeInvalid, refused.Code);

        // Identical to what an entirely made-up code gets, so this endpoint
        // cannot be used to find out whether a code is real.
        var nonsense = await Assert.ThrowsAsync<MarketplaceException>(
            () => w.Service.RedeemAsync(stranger, "ZZZZ-ZZZZ-ZZZZ"));

        Assert.Equal(refused.Code, nonsense.Code);
        Assert.Equal(refused.Status, nonsense.Status);
        Assert.Equal(refused.Message, nonsense.Message);
    }

    [SkippableFact]
    public async Task Scanning_twice_pays_once()
    {
        var w = await SetUpAsync();
        var listing = await w.Service.PublishAsync(w.Seller, AnItem());
        var placed = await w.Service.PlaceOrderAsync(w.Buyer, Guid.Parse(listing.Id));

        await w.Service.RedeemAsync(w.Seller, placed.Code);
        var again = await w.Service.RedeemAsync(w.Seller, placed.Code);

        Assert.Equal(OrderStatuses.Released, again.Status);
        Assert.Equal(9900, w.Ledger.Balance(AccountRef.User(w.Seller, Currency.Usd)));

        // Two settlement postings would have paid twice out of an account that
        // is allowed to go negative — which would not have bounced.
        Assert.Single(
            w.Ledger.Posted,
            p => p.IdempotencyKey == MarketplaceService.SettleKey(Guid.Parse(placed.Id)));
    }

    [SkippableFact]
    public async Task Cancelling_gives_the_buyer_their_money_back_and_puts_the_item_back_on_offer()
    {
        var w = await SetUpAsync();
        var listing = await w.Service.PublishAsync(w.Seller, AnItem());
        var placed = await w.Service.PlaceOrderAsync(w.Buyer, Guid.Parse(listing.Id));

        var cancelled = await w.Service.CancelAsync(w.Buyer, Guid.Parse(placed.Id));

        Assert.Equal(OrderStatuses.Cancelled, cancelled.Status);
        Assert.Equal(0, w.Ledger.Balance(AccountRef.Escrow(Currency.Usd)));
        Assert.Equal(0, w.Ledger.Balance(AccountRef.User(w.Buyer, Currency.Usd)));
        Assert.Equal(0, w.Ledger.Balance(AccountRef.Fees(Currency.Usd)));

        var after = await w.Service.ListingAsync(Guid.Parse(listing.Id));
        Assert.Equal(ListingStatuses.Active, after.Status);
    }

    [SkippableFact]
    public async Task Once_the_code_is_scanned_neither_party_can_cancel()
    {
        var w = await SetUpAsync();
        var listing = await w.Service.PublishAsync(w.Seller, AnItem());
        var placed = await w.Service.PlaceOrderAsync(w.Buyer, Guid.Parse(listing.Id));
        await w.Service.RedeemAsync(w.Seller, placed.Code);

        var refused = await Assert.ThrowsAsync<MarketplaceException>(
            () => w.Service.CancelAsync(w.Buyer, Guid.Parse(placed.Id)));

        Assert.Equal(MarketplaceErrors.OrderNotHeld, refused.Code);
        Assert.Equal(9900, w.Ledger.Balance(AccountRef.User(w.Seller, Currency.Usd)));
    }

    [SkippableFact]
    public async Task A_seller_cannot_buy_their_own_listing_and_the_item_stays_on_offer()
    {
        var w = await SetUpAsync();
        var listing = await w.Service.PublishAsync(w.Seller, AnItem());

        var refused = await Assert.ThrowsAsync<MarketplaceException>(
            () => w.Service.PlaceOrderAsync(w.Seller, Guid.Parse(listing.Id)));

        Assert.Equal(MarketplaceErrors.SelfPurchase, refused.Code);

        // The reservation is rolled back with the transaction that made it.
        var after = await w.Service.ListingAsync(Guid.Parse(listing.Id));
        Assert.Equal(ListingStatuses.Active, after.Status);
        Assert.Empty(w.Ledger.Posted);
    }

    [SkippableFact]
    public async Task A_second_buyer_is_refused_while_the_first_still_holds_it()
    {
        var w = await SetUpAsync();
        var other = $"other-{Guid.NewGuid():N}";
        w.Directory.Add(other);

        var listing = await w.Service.PublishAsync(w.Seller, AnItem());
        await w.Service.PlaceOrderAsync(w.Buyer, Guid.Parse(listing.Id));

        var refused = await Assert.ThrowsAsync<MarketplaceException>(
            () => w.Service.PlaceOrderAsync(other, Guid.Parse(listing.Id)));

        Assert.Equal(MarketplaceErrors.ListingUnavailable, refused.Code);
    }

    [SkippableFact]
    public async Task A_buyer_who_cannot_afford_it_leaves_the_item_on_offer()
    {
        var w = await SetUpAsync();
        var listing = await w.Service.PublishAsync(w.Seller, AnItem());
        w.Ledger.RefuseWith = Usd("4.00");

        var refused = await Assert.ThrowsAsync<MarketplaceException>(
            () => w.Service.PlaceOrderAsync(w.Buyer, Guid.Parse(listing.Id)));

        Assert.Equal(MarketplaceErrors.InsufficientFunds, refused.Code);
        Assert.Equal("USD", refused.Facts["currency"]);

        var after = await w.Service.ListingAsync(Guid.Parse(listing.Id));
        Assert.Equal(ListingStatuses.Active, after.Status);

        // And the failed attempt left no row to hold the listing's one live
        // claim, so the next buyer can have it.
        await using var db = _fixture.Context();
        Assert.False(await db.Orders.AnyAsync(o => o.ListingId == Guid.Parse(listing.Id)));
    }

    [SkippableFact]
    public async Task An_unverified_phone_cannot_lock_anyone_elses_money()
    {
        var w = await SetUpAsync();
        var unverified = $"unverified-{Guid.NewGuid():N}";
        w.Directory.Add(unverified, "Caro", phoneVerified: false);

        var listing = await w.Service.PublishAsync(w.Seller, AnItem());

        var refused = await Assert.ThrowsAsync<MarketplaceException>(
            () => w.Service.PlaceOrderAsync(unverified, Guid.Parse(listing.Id)));

        Assert.Equal(MarketplaceErrors.PhoneNotVerified, refused.Code);
        Assert.Empty(w.Ledger.Posted);
    }

    [SkippableFact]
    public async Task Publishing_needs_no_verified_phone()
    {
        var w = await SetUpAsync();
        var unverified = $"unverified-{Guid.NewGuid():N}";
        w.Directory.Add(unverified, "Caro", phoneVerified: false);

        // D11 gates money, not speech. Blocking this would leave the market
        // empty for a check that happens anyway when somebody pays.
        var listing = await w.Service.PublishAsync(unverified, AnItem());

        Assert.Equal(ListingStatuses.Active, listing.Status);
    }

    [SkippableFact]
    public async Task A_commission_that_rounds_to_nothing_is_not_charged()
    {
        var w = await SetUpAsync();

        // 1% of twelve cents is a hundredth of a cent, and USD is accounted in
        // cents. The fee leg has to be left off rather than posted as zero,
        // which the ledger refuses — the same bug conversions had.
        var listing = await w.Service.PublishAsync(w.Seller, AnItem("0.12"));
        var placed = await w.Service.PlaceOrderAsync(w.Buyer, Guid.Parse(listing.Id));
        var released = await w.Service.RedeemAsync(w.Seller, placed.Code);

        Assert.Equal(Money.Zero(Currency.Usd), released.Fee);
        Assert.Equal(12, w.Ledger.Balance(AccountRef.User(w.Seller, Currency.Usd)));

        var settlement = w.Ledger.Posted.Last();
        Assert.DoesNotContain(settlement.Legs, l => l.Amount.IsZero);
    }

    [SkippableFact]
    public async Task A_seller_cannot_withdraw_an_item_somebody_has_money_locked_against()
    {
        var w = await SetUpAsync();
        var listing = await w.Service.PublishAsync(w.Seller, AnItem());
        await w.Service.PlaceOrderAsync(w.Buyer, Guid.Parse(listing.Id));

        var refused = await Assert.ThrowsAsync<MarketplaceException>(
            () => w.Service.WithdrawAsync(w.Seller, Guid.Parse(listing.Id)));

        // Otherwise the buyer's balance is stranded until the hold times out.
        Assert.Equal(MarketplaceErrors.ListingUnavailable, refused.Code);
    }
}

/// <summary>A clock a test can move.</summary>
public sealed class FakeClock : TimeProvider
{
    private DateTimeOffset _now;

    public FakeClock(DateTimeOffset now) => _now = now;

    public override DateTimeOffset GetUtcNow() => _now;

    public void Advance(TimeSpan by) => _now += by;
}
