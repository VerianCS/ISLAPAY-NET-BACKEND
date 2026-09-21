using IslaPay.Ledger.Domain;
using IslaPay.Platform;
using IslaPay.TestSupport;
using Npgsql;

namespace IslaPay.Ledger.Tests;

/// <summary>
/// The ledger against a real Postgres.
/// </summary>
/// <remarks>
/// The domain's property tests already prove the rules hold in memory. These
/// prove the storage does not quietly break them — which is a different
/// question, and the one that decides whether the numbers survive a restart,
/// a concurrent request, or somebody with a psql prompt.
/// </remarks>
[Collection(LedgerSchemaDefinition.Name)]
[Trait("Category", "Integration")]
public class PostgresLedgerTests
{
    private readonly LedgerSchemaFixture _postgres;

    public PostgresLedgerTests(LedgerSchemaFixture postgres) => _postgres = postgres;

    private PostgresLedger Ledger()
    {
        Skip.IfNot(_postgres.Available, "No Postgres reachable.");
        return _postgres.Ledger();
    }

    private static string NewUser() => $"u{Guid.NewGuid():N}"[..12];

    [SkippableFact]
    public async Task Opening_accounts_is_idempotent()
    {
        var ledger = Ledger();
        var user = NewUser();

        await ledger.EnsureUserAccountsAsync(user, [Currency.EIsla, Currency.Usdt]);
        // Called again from the lazy path on first read, and from a redelivered
        // event. Neither must double anything.
        await ledger.EnsureUserAccountsAsync(user, [Currency.EIsla, Currency.Usdt]);

        var balances = await ledger.BalancesAsync(user);

        Assert.Equal(2, balances.Count);
        Assert.All(balances, b => Assert.Equal(0, b.Balance.MinorUnits));
    }

    [SkippableFact]
    public async Task A_new_account_shows_zero_rather_than_nothing()
    {
        var ledger = Ledger();
        var user = NewUser();

        await ledger.EnsureUserAccountsAsync(user, [Currency.EIsla]);
        var balances = await ledger.BalancesAsync(user);

        // A wallet that hides a zero balance looks broken to someone who has
        // just signed up.
        Assert.Equal(Currency.EIsla, Assert.Single(balances).Currency);
    }

    [SkippableFact]
    public async Task A_posting_moves_money_and_still_sums_to_zero()
    {
        var ledger = Ledger();
        var user = NewUser();
        await ledger.EnsureUserAccountsAsync(user, [Currency.EIsla]);

        await ledger.PostAsync(Deposit(user, "100.00"));

        var balance = Assert.Single(await ledger.BalancesAsync(user));
        Assert.Equal("100.00", balance.Balance.ToString());

        Assert.Equal(0, await SumOfEveryEntryAsync(Currency.EIsla));
    }

    [SkippableFact]
    public async Task A_balance_always_equals_the_fold_of_its_own_entries()
    {
        var ledger = Ledger();
        var user = NewUser();
        await ledger.EnsureUserAccountsAsync(user, [Currency.EIsla]);

        for (var i = 0; i < 20; i++)
            await ledger.PostAsync(Deposit(user, "10.00"));

        // The materialised balance is a cache of a fold. The moment it can
        // disagree with the entries, every figure the app shows is a guess.
        var drift = await DriftAsync();
        Assert.Equal(0, drift);
    }

    [SkippableFact]
    public async Task An_idempotency_key_never_moves_money_twice()
    {
        var ledger = Ledger();
        var user = NewUser();
        await ledger.EnsureUserAccountsAsync(user, [Currency.EIsla]);

        var key = Guid.NewGuid().ToString("N");

        var first = await ledger.PostAsync(Deposit(user, "50.00", key));
        var replay = await ledger.PostAsync(Deposit(user, "50.00", key));

        Assert.True(first.Written);
        Assert.False(replay.Written);
        Assert.Equal(first.Id, replay.Id);

        var balance = Assert.Single(await ledger.BalancesAsync(user));
        Assert.Equal("50.00", balance.Balance.ToString());
    }

    [SkippableFact]
    public async Task A_user_account_cannot_be_overdrawn()
    {
        var ledger = Ledger();
        var user = NewUser();
        await ledger.EnsureUserAccountsAsync(user, [Currency.EIsla]);
        await ledger.PostAsync(Deposit(user, "10.00"));

        await Assert.ThrowsAsync<InsufficientFundsException>(
            () => ledger.PostAsync(Withdraw(user, "10.01")));

        // And the refused posting left nothing behind — not the entry it would
        // have written, and not a balance that moved.
        var balance = Assert.Single(await ledger.BalancesAsync(user));
        Assert.Equal("10.00", balance.Balance.ToString());
        Assert.Equal(0, await DriftAsync());
    }

    [SkippableFact]
    public async Task Concurrent_postings_on_one_account_do_not_lose_an_update()
    {
        var ledger = Ledger();
        var user = NewUser();
        await ledger.EnsureUserAccountsAsync(user, [Currency.EIsla]);

        // Warm the connection pool first, and this is not a tidiness detail.
        //
        // With a cold pool each task waits on its own connection handshake,
        // which staggers the starts enough that the twenty postings barely
        // overlap and the test passes whether or not the balance rows are
        // locked. Measured: cold, this test passed with the FOR UPDATE removed;
        // warm, 19 of 20 overlap, 15 collide on the sequence number and the
        // balance lands on 6.00 instead of 21.00.
        await WarmThePoolAsync();
        await ledger.PostAsync(Deposit(user, "1.00"));

        // The classic lost update: twenty writers read the same balance and
        // each adds to what it read.
        await Task.WhenAll(Enumerable.Range(0, 20)
            .Select(_ => ledger.PostAsync(Deposit(user, "1.00"))));

        var balance = Assert.Single(await ledger.BalancesAsync(user));
        Assert.Equal("21.00", balance.Balance.ToString());
        Assert.Equal(0, await DriftAsync());
    }

    [SkippableFact]
    public async Task Sequence_numbers_are_gapless_per_account()
    {
        var ledger = Ledger();
        var user = NewUser();
        await ledger.EnsureUserAccountsAsync(user, [Currency.EIsla]);

        await WarmThePoolAsync();
        await Task.WhenAll(Enumerable.Range(0, 15)
            .Select(_ => ledger.PostAsync(Deposit(user, "1.00"))));

        await using var connection = await _postgres.Database.OpenAsync();
        await using var command = new NpgsqlCommand("""
            SELECT count(*), min(e.seq), max(e.seq)
            FROM ledger.entries e
            JOIN ledger.accounts a ON a.id = e.account_id
            WHERE a.owner = @owner;
            """, connection);
        command.Parameters.AddWithValue("owner", user);

        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());

        // A gap is a removed entry, which is how a deletion is detected even
        // if it happened outside this database.
        Assert.Equal(15, reader.GetInt64(0));
        Assert.Equal(1, reader.GetInt64(1));
        Assert.Equal(15, reader.GetInt64(2));
    }

    [SkippableFact]
    public async Task An_entry_cannot_be_edited_or_removed()
    {
        var ledger = Ledger();
        var user = NewUser();
        await ledger.EnsureUserAccountsAsync(user, [Currency.EIsla]);
        await ledger.PostAsync(Deposit(user, "5.00"));

        await using var connection = await _postgres.Database.OpenAsync();

        // Not the application refusing — the database refusing, which is the
        // only refusal that still applies to whoever has a psql prompt.
        await using var update = new NpgsqlCommand(
            "UPDATE ledger.entries SET minor_units = 999999;", connection);
        var edit = await Assert.ThrowsAsync<PostgresException>(() => update.ExecuteNonQueryAsync());
        Assert.Contains("append-only", edit.MessageText, StringComparison.Ordinal);

        await using var delete = new NpgsqlCommand("DELETE FROM ledger.entries;", connection);
        var removal = await Assert.ThrowsAsync<PostgresException>(() => delete.ExecuteNonQueryAsync());
        Assert.Contains("append-only", removal.MessageText, StringComparison.Ordinal);
    }

    [SkippableFact]
    public async Task History_pages_newest_first_without_repeating_or_skipping()
    {
        var ledger = Ledger();
        var user = NewUser();
        await ledger.EnsureUserAccountsAsync(user, [Currency.EIsla]);

        for (var i = 1; i <= 7; i++)
            await ledger.PostAsync(Deposit(user, $"{i}.00"));

        var seen = new List<long>();
        string? cursor = null;
        do
        {
            var page = await ledger.EntriesAsync(user, limit: 3, cursor);
            seen.AddRange(page.Items.Select(e => e.Id));
            cursor = page.NextCursor;
        }
        while (cursor is not null);

        Assert.Equal(7, seen.Count);
        Assert.Equal(seen.Count, seen.Distinct().Count());
        // Newest first, all the way through, across page boundaries.
        Assert.Equal(seen.OrderByDescending(id => id), seen);
    }

    [SkippableFact]
    public async Task A_cursor_we_never_issued_starts_from_the_beginning()
    {
        var ledger = Ledger();
        var user = NewUser();
        await ledger.EnsureUserAccountsAsync(user, [Currency.EIsla]);
        await ledger.PostAsync(Deposit(user, "1.00"));

        // Almost always a stale link, and a 500 is a worse answer than the
        // first page.
        var page = await ledger.EntriesAsync(user, limit: 10, cursor: "not-a-cursor");

        Assert.Single(page.Items);
    }

    [SkippableFact]
    public async Task Entries_carry_the_postings_kind_and_metadata()
    {
        var ledger = Ledger();
        var user = NewUser();
        await ledger.EnsureUserAccountsAsync(user, [Currency.EIsla]);
        await ledger.PostAsync(Deposit(user, "12.00"));

        var entry = Assert.Single((await ledger.EntriesAsync(user, 10)).Items);

        Assert.Equal("settlement", entry.Kind);
        Assert.Equal("zelle", entry.Metadata["method"]);
        Assert.Equal("12.00", entry.Amount.ToString());
    }

    // ------------------------------------------------------------------ helpers

    private static Posting Deposit(string user, string amount, string? key = null) =>
        new(
            Guid.NewGuid(),
            TransactionKind.Settlement,
            [
                new Leg(AccountId.User(user, Currency.EIsla), Money.Parse(amount, Currency.EIsla)),
                new Leg(AccountId.CashFloat(Currency.EIsla), Money.Parse($"-{amount}", Currency.EIsla)),
            ],
            DateTimeOffset.UtcNow,
            idempotencyKey: key,
            metadata: new Dictionary<string, string> { ["method"] = "zelle" });

    private static Posting Withdraw(string user, string amount) =>
        new(
            Guid.NewGuid(),
            TransactionKind.Settlement,
            [
                new Leg(AccountId.User(user, Currency.EIsla), Money.Parse($"-{amount}", Currency.EIsla)),
                new Leg(AccountId.CashFloat(Currency.EIsla), Money.Parse(amount, Currency.EIsla)),
            ],
            DateTimeOffset.UtcNow);

    /// <summary>
    /// Opens and returns enough connections that the pool has them ready.
    /// </summary>
    /// <remarks>
    /// Concurrency tests that race a cold pool race the handshake instead of
    /// the database, and pass for a reason that has nothing to do with the
    /// code under test.
    /// </remarks>
    private async Task WarmThePoolAsync()
    {
        var connections = new List<NpgsqlConnection>();
        for (var i = 0; i < 24; i++)
            connections.Add(await _postgres.Database.OpenAsync());
        foreach (var connection in connections)
            await connection.DisposeAsync();
    }

    /// <summary>Every entry in a currency, summed. Must be zero: nothing is created.</summary>
    private async Task<long> SumOfEveryEntryAsync(Currency currency)
    {
        await using var connection = await _postgres.Database.OpenAsync();
        // Cast back to bigint: sum() over a bigint column returns numeric, so
        // that Postgres can hold a total wider than the column. .NET maps that
        // to decimal, and the cast keeps the assertion about integers.
        await using var command = new NpgsqlCommand(
            "SELECT COALESCE(sum(minor_units), 0)::bigint FROM ledger.entries WHERE currency = @c;",
            connection);
        command.Parameters.AddWithValue("c", currency.Code());
        return (long)(await command.ExecuteScalarAsync())!;
    }

    /// <summary>How many accounts disagree with the fold of their own entries.</summary>
    private async Task<long> DriftAsync()
    {
        await using var connection = await _postgres.Database.OpenAsync();
        await using var command = new NpgsqlCommand("""
            SELECT count(*)
            FROM ledger.balances b
            JOIN (
                SELECT account_id, COALESCE(sum(minor_units), 0) AS folded, count(*) AS entries
                FROM ledger.entries GROUP BY account_id
            ) e ON e.account_id = b.account_id
            WHERE b.minor_units <> e.folded OR b.entry_count <> e.entries;
            """, connection);
        return (long)(await command.ExecuteScalarAsync())!;
    }
}
