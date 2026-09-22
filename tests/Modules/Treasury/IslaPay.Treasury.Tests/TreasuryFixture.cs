using IslaPay.Ledger;
using IslaPay.Platform;
using IslaPay.Platform.AspNet;
using IslaPay.Platform.Data;
using IslaPay.Platform.Messaging;
using IslaPay.TestSupport;

namespace IslaPay.Treasury.Tests;

/// <summary>
/// A throwaway database with the ledger's schema, and a treasury over it.
/// </summary>
/// <remarks>
/// The treasury owns no schema of its own — it reads the ledger's — so there
/// is nothing else to migrate. What varies between tests is which escrow
/// reporters are present, which is the only thing the reconciliation depends
/// on besides the book itself.
/// </remarks>
public sealed class TreasuryFixture : PostgresFixture
{
    protected override Task AfterCreateAsync() =>
        Migrator.ApplyAsync(Database,
        [
            new MigrationSet("ledger", typeof(LedgerModule).Assembly, "IslaPay.Ledger.Migrations."),
            new MigrationSet("messaging", typeof(Outbox).Assembly,
                "IslaPay.Platform.Messaging.Migrations."),
        ]);

    public PostgresLedger Ledger() => new(Database, new Outbox(Database), new TestCatalog());

    public TreasuryService Treasury(params IEscrowReporter[] reporters) =>
        new(Ledger(), new TestCatalog(), reporters, TimeProvider.System);
}

[CollectionDefinition(Name)]
public sealed class TreasuryDefinition : ICollectionFixture<TreasuryFixture>
{
    public const string Name = "treasury";
}

/// <summary>A context that says whatever a test needs it to say.</summary>
/// <remarks>
/// Deliberately not a real module's reporter. What is under test here is the
/// arithmetic of the band and the shape of the answer, and wiring a real
/// marketplace in would make a failure ambiguous between the two.
/// </remarks>
public sealed class StatedHoldings : IEscrowReporter
{
    private readonly List<EscrowHolding> _holdings = [];

    public StatedHoldings(string context) => Context = context;

    public string Context { get; }

    public StatedHoldings Holding(Currency currency, long minorUnits, long inFlight = 0)
    {
        _holdings.Add(new EscrowHolding(currency.Code, minorUnits, inFlight));
        return this;
    }

    public Task<IReadOnlyList<EscrowHolding>> OutstandingAsync(
        CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<EscrowHolding>>(_holdings);
}
