using System.Text;
using Npgsql;

namespace IslaPay.Ledger.Tests;

/// <summary>
/// The USD to EISLA rename, against rows that are actually in USD.
/// </summary>
/// <remarks>
/// <para>
/// Every other test here runs against a database created a moment earlier, so
/// <c>002_eisla.sql</c> runs over empty tables and proves only that it parses.
/// The whole risk of that script is the part no fresh database exercises: it
/// rewrites history, and it turns the append-only trigger off to do it.
/// </para>
/// <para>
/// So these tests put USD rows in by hand — as a database restored from before
/// the rename would have them — and run the shipped script over them. It is
/// the shipped one, read out of the assembly rather than retyped here, because
/// a test of a migration that is not the migration tests nothing. Running it a
/// second time is legitimate: every statement in it is a
/// <c>WHERE currency = 'USD'</c>, so a second run is a no-op, and that is
/// itself worth knowing.
/// </para>
/// </remarks>
[Collection(LedgerSchemaDefinition.Name)]
[Trait("Category", "Integration")]
public class EIslaRenameTests
{
    private readonly LedgerSchemaFixture _postgres;

    public EIslaRenameTests(LedgerSchemaFixture postgres) => _postgres = postgres;

    private static string Script() =>
        new StreamReader(
            typeof(LedgerModule).Assembly
                .GetManifestResourceStream("IslaPay.Ledger.Migrations.002_eisla.sql")
                ?? throw new InvalidOperationException("002_eisla.sql is not embedded."),
            Encoding.UTF8).ReadToEnd();

    private async Task<NpgsqlConnection> OpenAsync()
    {
        Skip.IfNot(_postgres.Available, "No Postgres reachable.");
        var connection = new NpgsqlConnection(_postgres.Options.ConnectionString);
        await connection.OpenAsync();
        return connection;
    }

    private static async Task ExecuteAsync(NpgsqlConnection connection, string sql)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<T?> ScalarAsync<T>(NpgsqlConnection connection, string sql)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        var value = await command.ExecuteScalarAsync();
        return value is null or DBNull ? default : (T)value;
    }

    /// <summary>
    /// A user, a mirror of money held outside, and one posting between them,
    /// all in USD — what a database taken before the rename looks like.
    /// </summary>
    /// <remarks>
    /// The counterparty is an <c>external</c> mirror rather than the float
    /// because the float is named <c>platform:float:USD</c> for everyone, and
    /// that name is unique: the second test to seed one would collide with the
    /// first. A mirror is named per rail, so each test can have its own.
    /// </remarks>
    private static async Task<Seed> SeedInTheOldCurrencyAsync(NpgsqlConnection connection)
    {
        var user = $"u{Guid.NewGuid():N}"[..12];
        var mirror = $"rail-{user}";
        var posting = Guid.NewGuid();

        await ExecuteAsync(connection, $"""
            INSERT INTO ledger.accounts
                (name, owner_type, owner, currency, account_type, may_go_negative)
            VALUES
                ('user:{user}:USD', 'user', '{user}', 'USD', 'liability', false),
                ('external:{mirror}:USD', 'external', '{mirror}', 'USD', 'asset', true);

            INSERT INTO ledger.postings (id, kind, posted_at)
            VALUES ('{posting}', 'deposit', now());

            INSERT INTO ledger.entries
                (posting_id, account_id, seq, currency, minor_units, occurred_at)
            SELECT '{posting}', a.id, 1, 'USD',
                   CASE WHEN a.owner_type = 'user' THEN 10000 ELSE -10000 END,
                   now()
              FROM ledger.accounts a
             WHERE a.owner IN ('{user}', '{mirror}');
            """);

        return new Seed(user, mirror);
    }

    /// <summary>One test's two accounts, so its assertions can exclude everyone else's.</summary>
    private sealed record Seed(string User, string Mirror)
    {
        /// <summary>A predicate over <c>ledger.accounts a</c>.</summary>
        public string Mine => $"a.owner IN ('{User}', '{Mirror}')";
    }

    [SkippableFact]
    public async Task The_rename_moves_accounts_entries_and_the_structured_name()
    {
        await using var connection = await OpenAsync();
        var seed = await SeedInTheOldCurrencyAsync(connection);

        await ExecuteAsync(connection, Script());

        Assert.Equal("EISLA", await ScalarAsync<string>(connection,
            $"SELECT currency FROM ledger.accounts a WHERE a.owner = '{seed.User}';"));

        // The name is what every reconciliation report and every support
        // conversation quotes, so it has to move with the currency.
        Assert.Equal($"user:{seed.User}:EISLA", await ScalarAsync<string>(connection,
            $"SELECT name FROM ledger.accounts a WHERE a.owner = '{seed.User}';"));
        Assert.Equal($"external:{seed.Mirror}:EISLA", await ScalarAsync<string>(connection,
            $"SELECT name FROM ledger.accounts a WHERE a.owner = '{seed.Mirror}';"));

        Assert.Equal(2L, await ScalarAsync<long>(connection, $"""
            SELECT count(*) FROM ledger.entries e
              JOIN ledger.accounts a ON a.id = e.account_id
             WHERE {seed.Mine} AND e.currency = 'EISLA';
            """));

        Assert.Equal(0L, await ScalarAsync<long>(connection,
            "SELECT count(*) FROM ledger.entries WHERE currency = 'USD';"));
    }

    /// <summary>
    /// Only the label moves. An amount that changed would be the one failure
    /// nobody would spot until a customer did.
    /// </summary>
    [SkippableFact]
    public async Task The_rename_moves_no_money()
    {
        await using var connection = await OpenAsync();
        var seed = await SeedInTheOldCurrencyAsync(connection);

        // Scoped to this test's accounts: the fixture is shared, and a sum
        // over the whole table would be asserting about other tests' money.
        var sum = $"""
            SELECT coalesce(sum(e.minor_units), 0)::bigint FROM ledger.entries e
              JOIN ledger.accounts a ON a.id = e.account_id
             WHERE {seed.Mine};
            """;
        var count = $"""
            SELECT count(*) FROM ledger.entries e
              JOIN ledger.accounts a ON a.id = e.account_id
             WHERE {seed.Mine};
            """;

        var before = await ScalarAsync<long>(connection, sum);
        var countBefore = await ScalarAsync<long>(connection, count);

        await ExecuteAsync(connection, Script());

        Assert.Equal(before, await ScalarAsync<long>(connection, sum));
        Assert.Equal(countBefore, await ScalarAsync<long>(connection, count));
        Assert.Equal(10000L, await ScalarAsync<long>(connection, $"""
            SELECT e.minor_units FROM ledger.entries e
              JOIN ledger.accounts a ON a.id = e.account_id
             WHERE a.owner = '{seed.User}';
            """));
    }

    /// <summary>
    /// The script turns the append-only trigger off. This is the test that it
    /// turns it back on — the failure it guards against is silent, and it
    /// would leave every later posting editable.
    /// </summary>
    [SkippableFact]
    public async Task History_is_append_only_again_afterwards()
    {
        await using var connection = await OpenAsync();
        await SeedInTheOldCurrencyAsync(connection);

        await ExecuteAsync(connection, Script());

        var refused = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(
            connection, "UPDATE ledger.entries SET minor_units = 1 WHERE currency = 'EISLA';"));

        // `restrict_violation`, as `ledger.refuse_mutation` raises it.
        Assert.Equal("23001", refused.SqlState);
        Assert.Contains("append-only", refused.MessageText, StringComparison.Ordinal);
    }

    [SkippableFact]
    public async Task Running_it_twice_changes_nothing_the_second_time()
    {
        await using var connection = await OpenAsync();
        var seed = await SeedInTheOldCurrencyAsync(connection);

        await ExecuteAsync(connection, Script());
        // Would throw if the second pass tried to rename an already-renamed
        // account — `user:42:EISLA` would become `user:42:EISEISLA` and the
        // script's own assertion would catch it.
        await ExecuteAsync(connection, Script());

        Assert.Equal($"user:{seed.User}:EISLA", await ScalarAsync<string>(connection,
            $"SELECT name FROM ledger.accounts a WHERE a.owner = '{seed.User}';"));
    }
}
