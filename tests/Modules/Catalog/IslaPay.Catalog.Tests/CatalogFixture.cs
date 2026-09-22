using IslaPay.Platform.Data;
using IslaPay.TestSupport;
using Npgsql;

namespace IslaPay.Catalog.Tests;

/// <summary>
/// A throwaway database with the catalogue's schema and its seed applied.
/// </summary>
/// <remarks>
/// The seed is part of the migration, not fixture setup, so these tests read
/// the rows a fresh deployment actually gets. A fixture that inserted its own
/// currencies would be testing a catalogue nobody ships.
/// </remarks>
public sealed class CatalogFixture : PostgresFixture
{
    protected override Task AfterCreateAsync() => Migrator.ApplyAsync(Database,
    [
        new MigrationSet("catalog", typeof(CatalogModule).Assembly, "IslaPay.Catalog.Migrations."),
    ]);

    /// <summary>The real catalogue, loaded from the real table.</summary>
    public async Task<PostgresCurrencyCatalog> LoadedAsync()
    {
        var catalog = new PostgresCurrencyCatalog(Database);
        await catalog.RefreshAsync();
        return catalog;
    }

    /// <summary>
    /// One statement, for the tests that change a row on purpose.
    /// </summary>
    /// <remarks>
    /// Switching a currency on is what an operator does through the admin
    /// endpoint; doing it in SQL here checks the half these tests are about —
    /// that a refresh sees it — without dragging authentication in.
    /// </remarks>
    public async Task ExecuteAsync(string sql)
    {
        await using var connection = await Database.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }
}

[CollectionDefinition(Name)]
public sealed class CatalogDefinition : ICollectionFixture<CatalogFixture>
{
    public const string Name = "catalog-schema";
}
