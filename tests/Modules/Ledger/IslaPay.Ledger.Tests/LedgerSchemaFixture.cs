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

    /// <summary>The ledger under test, with a real outbox behind it.</summary>
    public PostgresLedger Ledger() => new(Database, new Outbox(Database));
}

[CollectionDefinition(Name)]
public sealed class LedgerSchemaDefinition : ICollectionFixture<LedgerSchemaFixture>
{
    public const string Name = "ledger-schema";
}
