using IslaPay.TestSupport;
using IslaPay.Catalog.Contracts;
using IslaPay.Custody.Contracts;
using IslaPay.Ledger.Contracts;
using IslaPay.Platform;
using Microsoft.EntityFrameworkCore;

namespace IslaPay.Custody.Tests;

/// <summary>
/// Money arriving from a chain.
/// </summary>
/// <remarks>
/// The two rules everything here is about: nothing reaches the ledger before
/// the chain is final, and what does reach it lands exactly once however many
/// times the scanner says the same thing.
/// </remarks>
[Collection(CustodyDefinition.Name)]
public sealed class DepositTests : IAsyncLifetime
{
    private static readonly CurrencyOnNetwork Tron = CustodyFixture.Tron;

    private readonly CustodyFixture _fixture;

    public DepositTests(CustodyFixture fixture) => _fixture = fixture;

    public Task InitializeAsync() => _fixture.ResetAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    private static Money Usdt(string amount) => Money.Parse(amount, TestCurrencies.Usdt);

    private sealed record World(
        CustodyService Service, FakeLedger Ledger, string User, string Address, FakeClock Clock);

    private async Task<World> SetUpAsync(bool phoneVerified = true)
    {
        Skip.IfNot(_fixture.Available, "Postgres is not reachable.");

        var directory = new FakeDirectory();
        var user = $"user-{Guid.NewGuid():N}";
        directory.Add(user, "Ana", phoneVerified);

        var clock = new FakeClock(DateTimeOffset.Parse("2026-02-01T10:00:00Z", null));
        var service = _fixture.Service(new FakeLedger(), directory, clock: clock);
        var ledger = new FakeLedger();
        service = _fixture.Service(ledger, directory, clock: clock);

        var address = phoneVerified
            ? (await service.AddressAsync(user, CurrencyCodes.Usdt, Tron.NetworkId)).Address
            : string.Empty;

        return new World(service, ledger, user, address, clock);
    }

    private static ObservedTransfer Seen(
        World w, string amount = "100.000000", int confirmations = 0, string? tx = null,
        int output = 0) =>
        new(
            Network: Tron.NetworkId,
            Address: w.Address,
            TxHash: tx ?? "0x" + Guid.NewGuid().ToString("N"),
            Amount: Usdt(amount),
            Confirmations: confirmations,
            OutputIndex: output);

    // ------------------------------------------------------------- addresses

    [SkippableFact]
    public async Task An_address_is_issued_once_and_then_reused()
    {
        var w = await SetUpAsync();

        var again = await w.Service.AddressAsync(w.User, CurrencyCodes.Usdt, Tron.NetworkId);

        Assert.Equal(w.Address, again.Address);
        Assert.Equal(Tron.Confirmations, again.Confirmations);
        Assert.Equal("USDT", again.Currency);

        await using var db = _fixture.Context();
        Assert.Equal(1, await db.Addresses.CountAsync(a => a.UserId == w.User));
    }

    [SkippableFact]
    public async Task An_address_is_checked_against_the_network_before_it_is_shown()
    {
        Skip.IfNot(_fixture.Available, "Postgres is not reachable.");

        var directory = new FakeDirectory();
        var user = $"user-{Guid.NewGuid():N}";
        directory.Add(user);

        // A custodian that answers with a Bitcoin address, or a truncated one,
        // or an error page. Whatever the cause, showing it would send somebody
        // else's money somewhere unrecoverable.
        var service = _fixture.Service(
            new FakeLedger(), directory, new FixedAddresses("1A1zP1eP5QGefi2DMPTfTL5SLmv7Divf"));

        var refused = await Assert.ThrowsAsync<CustodyException>(
            () => service.AddressAsync(user, CurrencyCodes.Usdt, Tron.NetworkId));

        Assert.Equal(CustodyErrors.AddressUnavailable, refused.Code);

        await using var db = _fixture.Context();
        Assert.False(await db.Addresses.AnyAsync(a => a.UserId == user));
    }

    [SkippableFact]
    public async Task An_unproved_phone_gets_no_address()
    {
        var w = await SetUpAsync(phoneVerified: false);

        var refused = await Assert.ThrowsAsync<CustodyException>(
            () => w.Service.AddressAsync(w.User, CurrencyCodes.Usdt, Tron.NetworkId));

        Assert.Equal(CustodyErrors.PhoneNotVerified, refused.Code);
    }

    [SkippableFact]
    public async Task A_chain_nobody_has_heard_of_is_a_404()
    {
        var w = await SetUpAsync();

        var refused = await Assert.ThrowsAsync<CustodyException>(
            () => w.Service.AddressAsync(w.User, CurrencyCodes.Usdt, "dogecoin"));

        Assert.Equal(CustodyErrors.UnknownNetwork, refused.Code);
        Assert.Equal(404, refused.Status);
    }

    /// <summary>
    /// A chain that exists and is switched off is refused differently.
    /// </summary>
    /// <remarks>
    /// The distinction only became expressible when networks became rows.
    /// Ethereum is listed, USDT is listed on it, and this build does not watch
    /// it — so the request was well formed and the answer is no, which is a
    /// 422 and not a 404. Telling the client "no such chain" would be a lie
    /// that stops being true the day the switch is flipped.
    /// </remarks>
    [SkippableFact]
    public async Task A_chain_that_is_switched_off_is_a_422()
    {
        var w = await SetUpAsync();

        var refused = await Assert.ThrowsAsync<CustodyException>(
            () => w.Service.AddressAsync(w.User, CurrencyCodes.Usdt, "ethereum"));

        Assert.Equal(CustodyErrors.CurrencyNotOnNetwork, refused.Code);
        Assert.Equal(422, refused.Status);
    }

    /// <summary>An asset that is on no chain at all cannot be deposited.</summary>
    [SkippableFact]
    public async Task An_asset_that_is_not_on_the_chain_is_refused()
    {
        var w = await SetUpAsync();

        var refused = await Assert.ThrowsAsync<CustodyException>(
            () => w.Service.AddressAsync(w.User, CurrencyCodes.EIsla, Tron.NetworkId));

        Assert.Equal(CustodyErrors.CurrencyNotOnNetwork, refused.Code);
    }

    // -------------------------------------------------------------- finality

    [SkippableFact]
    public async Task A_shallow_deposit_is_recorded_and_nothing_is_posted()
    {
        var w = await SetUpAsync();

        var deposit = await w.Service.ObserveAsync(Seen(w, confirmations: 3));

        Assert.NotNull(deposit);
        Assert.Equal(DepositStatuses.Confirming, deposit!.Status);
        Assert.Equal(3, deposit.Confirmations);
        Assert.Equal(Tron.Confirmations, deposit.RequiredConfirmations);

        // The whole point of the module. Three blocks deep is a transfer that
        // can still be un-happened.
        Assert.Empty(w.Ledger.Posted);
    }

    [SkippableFact]
    public async Task A_final_deposit_lands_in_the_balance()
    {
        var w = await SetUpAsync();
        var tx = "0xabc";

        await w.Service.ObserveAsync(Seen(w, confirmations: 3, tx: tx));
        var credited = await w.Service.ObserveAsync(
            Seen(w, confirmations: Tron.Confirmations, tx: tx));

        Assert.Equal(DepositStatuses.Credited, credited!.Status);
        Assert.NotNull(credited.CreditedAt);

        var posting = Assert.Single(w.Ledger.Posted);
        Assert.Equal(CustodyService.CreditKey(Guid.Parse(credited.Id)), posting.IdempotencyKey);

        // The mirror convention: negative is money received from outside.
        Assert.Equal(-100_000_000, w.Ledger.Balance(
            AccountRef.External(Tron.NetworkId, TestCurrencies.Usdt)));
        Assert.Equal(100_000_000, w.Ledger.Balance(
            AccountRef.User(w.User, TestCurrencies.Usdt)));
    }

    [SkippableFact]
    public async Task Confirmations_only_ever_go_up()
    {
        var w = await SetUpAsync();
        var tx = "0xdef";

        await w.Service.ObserveAsync(Seen(w, confirmations: 10, tx: tx));

        // A scanner re-reading an older block must not walk a deposit
        // backwards — and must certainly not un-credit one.
        var back = await w.Service.ObserveAsync(Seen(w, confirmations: 2, tx: tx));

        Assert.Equal(10, back!.Confirmations);
    }

    // -------------------------------------------------------- exactly once

    [SkippableFact]
    public async Task Seeing_the_same_transfer_a_hundred_times_credits_it_once()
    {
        var w = await SetUpAsync();
        var tx = "0x" + Guid.NewGuid().ToString("N");

        for (var i = 0; i < 100; i++)
        {
            await w.Service.ObserveAsync(
                Seen(w, confirmations: Tron.Confirmations + i, tx: tx));
        }

        Assert.Single(w.Ledger.Posted);
        Assert.Equal(100_000_000, w.Ledger.Balance(AccountRef.User(w.User, TestCurrencies.Usdt)));

        await using var db = _fixture.Context();
        Assert.Equal(1, await db.Deposits.CountAsync(d => d.UserId == w.User));
    }

    [SkippableFact]
    public async Task One_transaction_paying_twice_is_two_deposits()
    {
        var w = await SetUpAsync();
        var tx = "0x" + Guid.NewGuid().ToString("N");

        await w.Service.ObserveAsync(
            Seen(w, "40.000000", Tron.Confirmations, tx, output: 0));
        await w.Service.ObserveAsync(
            Seen(w, "60.000000", Tron.Confirmations, tx, output: 1));

        Assert.Equal(2, w.Ledger.Posted.Count);
        Assert.Equal(100_000_000, w.Ledger.Balance(AccountRef.User(w.User, TestCurrencies.Usdt)));
    }

    [SkippableFact]
    public async Task A_transfer_to_an_address_we_do_not_know_is_ignored()
    {
        var w = await SetUpAsync();

        var stranger = w with { Address = "TJRyWwFs9wTFGZg3JbrVriFbNfCug5tDeC" };
        var nothing = await w.Service.ObserveAsync(Seen(stranger, confirmations: 30));

        // Not an error: a scanner watching more than it needs to, or somebody
        // sending to a stale address, is no reason to stop reading a block.
        Assert.Null(nothing);
        Assert.Empty(w.Ledger.Posted);
    }

    // -------------------------------------------------------------- reorgs

    [SkippableFact]
    public async Task A_deposit_that_vanishes_before_finality_leaves_no_trace_in_the_ledger()
    {
        var w = await SetUpAsync();
        var tx = "0x" + Guid.NewGuid().ToString("N");

        await w.Service.ObserveAsync(Seen(w, confirmations: 5, tx: tx));
        Assert.True(await w.Service.OrphanAsync(Tron.NetworkId, tx));

        var deposits = await w.Service.DepositsAsync(w.User);
        Assert.Equal(DepositStatuses.Orphaned, Assert.Single(deposits).Status);
        Assert.Empty(w.Ledger.Posted);
    }

    [SkippableFact]
    public async Task A_credited_deposit_is_never_orphaned()
    {
        var w = await SetUpAsync();
        var tx = "0x" + Guid.NewGuid().ToString("N");

        await w.Service.ObserveAsync(Seen(w, confirmations: Tron.Confirmations, tx: tx));

        // Past finality the loss, if there ever were one, is real. Quietly
        // flipping a status would leave the ledger and this table disagreeing
        // about money that is already in somebody's balance.
        Assert.False(await w.Service.OrphanAsync(Tron.NetworkId, tx));

        var deposits = await w.Service.DepositsAsync(w.User);
        Assert.Equal(DepositStatuses.Credited, Assert.Single(deposits).Status);
    }
}

/// <summary>A custodian that always answers the same thing.</summary>
internal sealed class FixedAddresses : IDepositAddresses
{
    private readonly string _address;

    public FixedAddresses(string address) => _address = address;

    public Task<IssuedAddress> IssueAsync(
        string userId, CurrencyOnNetwork network, CancellationToken cancellationToken = default) =>
        Task.FromResult(new IssuedAddress(_address, "fixed"));
}
