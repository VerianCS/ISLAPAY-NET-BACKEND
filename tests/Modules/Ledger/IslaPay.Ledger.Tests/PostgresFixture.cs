using IslaPay.Platform.Data;
using Npgsql;

namespace IslaPay.Ledger.Tests;

/// <summary>
/// A throwaway database, built by the real migrations.
/// </summary>
/// <remarks>
/// <para>
/// One database per run, dropped afterwards. Not one schema per test and not a
/// transaction rolled back at the end: the ledger's concurrency behaviour is
/// what these tests are for, and wrapping every test in an outer transaction
/// would hide exactly the row locking that is being checked.
/// </para>
/// <para>
/// Built by running <c>Migrator</c> over the module's own embedded scripts, so
/// what is tested is the schema that ships. A hand-written CREATE TABLE in a
/// fixture tests a schema nobody deploys.
/// </para>
/// </remarks>
public sealed class PostgresFixture : IAsyncLifetime
{
    private static string AdminConnectionString =>
        Environment.GetEnvironmentVariable("POSTGRES_URL")
        ?? "Host=localhost;Port=5432;Database=postgres;Username=postgres;Password=postgres";

    public string DatabaseName { get; } = $"islapay_test_{Guid.NewGuid():N}"[..24];

    public bool Available { get; private set; }

    public DataOptions Options => new()
    {
        ConnectionString = Rewrite(AdminConnectionString, DatabaseName),
        MigrateOnStartup = false,
    };

    public Database Database { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        try
        {
            await using var admin = new NpgsqlConnection(AdminConnectionString);
            await admin.OpenAsync();

            await using (var create = new NpgsqlCommand(
                $"CREATE DATABASE \"{DatabaseName}\";", admin))
            {
                await create.ExecuteNonQueryAsync();
            }
        }
        catch (Exception e) when (e is NpgsqlException or TimeoutException)
        {
            Available = false;
            return;
        }

        Database = new Database(Options);

        await Migrator.ApplyAsync(Database,
        [
            new MigrationSet("ledger", typeof(LedgerModule).Assembly, "IslaPay.Ledger.Migrations."),
        ]);

        Available = true;
    }

    public async Task DisposeAsync()
    {
        if (!Available) return;

        await Database.DisposeAsync();
        // The pool holds connections open, and Postgres refuses to drop a
        // database anything is connected to.
        NpgsqlConnection.ClearAllPools();

        try
        {
            await using var admin = new NpgsqlConnection(AdminConnectionString);
            await admin.OpenAsync();
            await using var drop = new NpgsqlCommand(
                $"DROP DATABASE IF EXISTS \"{DatabaseName}\" WITH (FORCE);", admin);
            await drop.ExecuteNonQueryAsync();
        }
        catch (NpgsqlException)
        {
            // A leftover database on a developer's machine is untidy, not a
            // test failure.
        }
    }

    private static string Rewrite(string connectionString, string database) =>
        new NpgsqlConnectionStringBuilder(connectionString) { Database = database }.ToString();
}

[CollectionDefinition(Name)]
public sealed class PostgresCollectionDefinition : ICollectionFixture<PostgresFixture>
{
    public const string Name = "postgres";
}
