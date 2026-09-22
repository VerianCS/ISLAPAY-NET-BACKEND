using IslaPay.Custody.Contracts;
using IslaPay.Ledger.Contracts;
using IslaPay.Platform;
using Microsoft.EntityFrameworkCore;

namespace IslaPay.Custody.Tests;

/// <summary>
/// What happens when the process dies between the ledger's commit and this
/// module's.
/// </summary>
/// <remarks>
/// The same window Marketplace and P2P have, and the same resolution: ask the
/// ledger rather than guess. What is different here is which way the two
/// guesses hurt. Assuming a credit landed when it did not leaves somebody
/// permanently short of money they really sent; assuming it did not leaves
/// IslaPay crediting the same chain transfer twice, which is money created
/// from nothing.
/// </remarks>
[Collection(CustodyDefinition.Name)]
public sealed class DepositRepairTests : IAsyncLifetime
{
    private static readonly CustodyNetwork Tron = CustodyNetworks.TronUsdt;

    private readonly CustodyFixture _fixture;

    public DepositRepairTests(CustodyFixture fixture) => _fixture = fixture;

    public Task InitializeAsync() => _fixture.ResetAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    private static Money Usdt(string amount) => Money.Parse(amount, Currency.Usdt);

    private sealed record World(
        CustodyService Service, FakeLedger Ledger, string User, string Address,
        FakeClock Clock, CustodyOptions Options);

    private async Task<World> SetUpAsync()
    {
        Skip.IfNot(_fixture.Available, "Postgres is not reachable.");

        var directory = new FakeDirectory();
        var user = $"user-{Guid.NewGuid():N}";
        directory.Add(user, "Ana");

        var ledger = new FakeLedger();
        var clock = new FakeClock(DateTimeOffset.Parse("2026-02-01T10:00:00Z", null));
        var service = _fixture.Service(ledger, directory, clock: clock);
        var address = (await service.AddressAsync(user, CustodyNetworks.Tron)).Address;

        return new World(service, ledger, user, address, clock, new CustodyOptions());
    }

    private static ObservedTransfer Final(World w, string tx, string amount = "100.000000") =>
        new(
            Network: CustodyNetworks.Tron,
            Address: w.Address,
            TxHash: tx,
            Amount: Usdt(amount),
            Confirmations: Tron.Confirmations);

    [SkippableFact]
    public async Task A_credit_that_landed_before_the_crash_is_adopted_not_re_posted()
    {
        var w = await SetUpAsync();

        // The ledger takes the posting and the caller then dies, so the row is
        // left saying `crediting` while the money really is in the balance.
        w.Ledger.FailAfterPosting = true;
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => w.Service.ObserveAsync(Final(w, "0xcrash")));

        Assert.Equal(100_000_000, w.Ledger.Balance(AccountRef.User(w.User, Currency.Usdt)));

        await using (var db = _fixture.Context())
        {
            var stuck = await db.Deposits.AsNoTracking().SingleAsync(d => d.UserId == w.User);
            Assert.Equal(DepositStatuses.Crediting, stuck.Status);
            Assert.Null(stuck.CreditPostingId);
        }

        w.Clock.Advance(w.Options.InFlightGrace + TimeSpan.FromMinutes(1));
        Assert.Equal(1, await w.Service.RepairAsync(w.Options.InFlightGrace));

        var repaired = Assert.Single(await w.Service.DepositsAsync(w.User));
        Assert.Equal(DepositStatuses.Credited, repaired.Status);

        // Adopted, not repeated.
        Assert.Single(w.Ledger.Posted);
        Assert.Equal(100_000_000, w.Ledger.Balance(AccountRef.User(w.User, Currency.Usdt)));
    }

    [SkippableFact]
    public async Task A_credit_that_never_landed_is_finished_rather_than_left_waiting()
    {
        var w = await SetUpAsync();
        await w.Service.ObserveAsync(Final(w, "0xnever") with { Confirmations = 2 });

        // Claimed, and then the ledger was never reached at all — the row
        // forced into the state a process that died between the two commits
        // would leave behind.
        await using (var db = _fixture.Context())
        {
            await db.Database.ExecuteSqlAsync(
                $"""
                 UPDATE custody.deposits
                    SET status = 'crediting', confirmations = required_confirmations
                  WHERE tx_hash = '0xnever'
                 """);
        }

        w.Clock.Advance(w.Options.InFlightGrace + TimeSpan.FromMinutes(1));
        Assert.Equal(1, await w.Service.RepairAsync(w.Options.InFlightGrace));

        // Finished, not merely released. A deposit this deep is one the
        // scanner has no reason to report again, so handing it back to
        // `confirming` would leave it waiting forever.
        var done = Assert.Single(await w.Service.DepositsAsync(w.User));
        Assert.Equal(DepositStatuses.Credited, done.Status);
        Assert.Single(w.Ledger.Posted);
        Assert.Equal(100_000_000, w.Ledger.Balance(AccountRef.User(w.User, Currency.Usdt)));
    }

    [SkippableFact]
    public async Task A_retry_after_a_release_still_credits_only_once()
    {
        var w = await SetUpAsync();
        var tx = "0xretry";

        w.Ledger.FailAfterPosting = true;
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => w.Service.ObserveAsync(Final(w, tx)));

        // A scanner pass arrives before the sweeper does. It resolves the
        // deposit itself rather than walking past it — and the shared key is
        // what stops it paying a second time.
        var again = await w.Service.ObserveAsync(Final(w, tx));

        Assert.Equal(DepositStatuses.Credited, again!.Status);
        Assert.Single(w.Ledger.Posted);
        Assert.Equal(100_000_000, w.Ledger.Balance(AccountRef.User(w.User, Currency.Usdt)));
    }

    [SkippableFact]
    public async Task Sweeping_leaves_a_deposit_that_is_still_waiting_alone()
    {
        var w = await SetUpAsync();
        await w.Service.ObserveAsync(Final(w, "0xshallow") with { Confirmations = 2 });

        w.Clock.Advance(TimeSpan.FromDays(30));

        // `confirming` is not a stuck state, however long it lasts — a chain
        // can be slow, and a sweeper that "helped" here would be crediting on
        // a timer instead of on finality.
        Assert.Equal(0, await w.Service.RepairAsync(w.Options.InFlightGrace));
        Assert.Empty(w.Ledger.Posted);
    }

    [SkippableFact]
    public async Task Sweeping_twice_settles_once()
    {
        var w = await SetUpAsync();

        w.Ledger.FailAfterPosting = true;
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => w.Service.ObserveAsync(Final(w, "0xtwice")));

        w.Clock.Advance(w.Options.InFlightGrace + TimeSpan.FromMinutes(1));

        Assert.Equal(1, await w.Service.RepairAsync(w.Options.InFlightGrace));
        Assert.Equal(0, await w.Service.RepairAsync(w.Options.InFlightGrace));
        Assert.Single(w.Ledger.Posted);
    }

    [SkippableFact]
    public async Task The_database_refuses_a_credit_that_is_not_final()
    {
        var w = await SetUpAsync();
        await w.Service.ObserveAsync(Final(w, "0xshallow") with { Confirmations = 4 });

        // The rule the module exists for, stated where a future caller with a
        // good reason has to argue with Postgres rather than with a comment.
        await using var db = _fixture.Context();
        var refused = await Assert.ThrowsAsync<Npgsql.PostgresException>(() =>
            db.Database.ExecuteSqlAsync(
                $"""
                 UPDATE custody.deposits
                    SET status = 'credited',
                        credit_posting_id = {Guid.NewGuid()},
                        credited_at = now()
                  WHERE tx_hash = '0xshallow'
                 """));

        Assert.Equal("deposits_are_only_credited_when_final", refused.ConstraintName);
    }
}
