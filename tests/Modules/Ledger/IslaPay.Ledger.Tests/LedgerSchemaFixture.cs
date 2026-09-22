using IslaPay.Platform.Data;
using IslaPay.Platform.Messaging;
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
            // The ledger writes events in the same transaction as the entries,
            // so its tests need the outbox table as well.
            new MigrationSet("messaging", typeof(Outbox).Assembly,
                "IslaPay.Platform.Messaging.Migrations."),
        ]);
    }

    /// <summary>
    /// The ledger under test, with a real outbox and the seeded catalogue.
    /// </summary>
    /// <remarks>
    /// The catalogue is not optional to it any more: the ledger is where the
    /// rule that no entry exists in an unlisted currency is actually enforced,
    /// so a ledger built without one could not enforce it.
    /// </remarks>
    public PostgresLedger Ledger() => new(Database, new Outbox(Database), new TestCatalog());
}

[CollectionDefinition(Name)]
public sealed class LedgerSchemaDefinition : ICollectionFixture<LedgerSchemaFixture>
{
    public const string Name = "ledger-schema";
}
