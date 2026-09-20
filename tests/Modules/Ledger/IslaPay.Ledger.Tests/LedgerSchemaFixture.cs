using IslaPay.Platform.Data;
using IslaPay.TestSupport;

namespace IslaPay.Ledger.Tests;

/// <summary>
/// A throwaway database with the ledger's own schema applied.
/// </summary>
/// <remarks>
/// Applied by running <c>Migrator</c> over the module's embedded scripts, so
/// what is tested is the schema that ships.
/// </remarks>
public sealed class LedgerSchemaFixture : PostgresFixture
{
    protected override async Task AfterCreateAsync()
    {
        await Migrator.ApplyAsync(Database,
        [
            new MigrationSet("ledger", typeof(LedgerModule).Assembly, "IslaPay.Ledger.Migrations."),
        ]);
    }
}

[CollectionDefinition(Name)]
public sealed class LedgerSchemaDefinition : ICollectionFixture<LedgerSchemaFixture>
{
    public const string Name = "ledger-schema";
}
