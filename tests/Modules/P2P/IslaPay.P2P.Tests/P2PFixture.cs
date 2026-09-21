using IslaPay.Identity.Contracts;
using IslaPay.Ledger.Contracts;
using IslaPay.Platform;
using IslaPay.Platform.Data;
using IslaPay.TestSupport;
using Microsoft.EntityFrameworkCore;

namespace IslaPay.P2P.Tests;

/// <summary>
/// A throwaway database with the P2P schema applied.
/// </summary>
/// <remarks>
/// Applied by running <c>Migrator</c> over the module's embedded script, so
/// what is tested is the schema that ships — including the check constraints,
/// which carry real rules here: a sell cannot wait on a payment, a settled
/// trade cannot lack a settled time.
/// </remarks>
public sealed class P2PFixture : PostgresFixture
{
    protected override Task AfterCreateAsync() => Migrator.ApplyAsync(Database,
    [
        new MigrationSet("p2p", typeof(P2PModule).Assembly, "IslaPay.P2P.Migrations."),
    ]);

    public P2PDbContext Context()
    {
        var builder = new DbContextOptionsBuilder<P2PDbContext>();
        builder.UseNpgsql(Options.ConnectionString);
        return new P2PDbContext(builder.Options);
    }

    /// <summary>
    /// Empties the trades and rates, and puts the seeded rail back as it was.
    /// </summary>
    /// <remarks>
    /// Before every test, and not for tidiness: <c>RepairAsync</c> sweeps the
    /// whole table by design, so a trade one test left behind is one the next
    /// test's sweeper acts on — against that test's own fake ledger, where it
    /// shows up as somebody else's money moving.
    /// </remarks>
    public async Task ResetAsync()
    {
        if (!Available) return;

        await using var db = Context();
        await db.Database.ExecuteSqlRawAsync(
            """
            TRUNCATE p2p.trades, p2p.rates RESTART IDENTITY;
            UPDATE p2p.methods SET available = false;
            """);
    }

    public P2PService Service(
        FakeLedger ledger,
        FakeDirectory directory,
        P2POptions? options = null,
        TimeProvider? clock = null) =>
        new(Context(), ledger, directory, options ?? new P2POptions(), clock);
}

[CollectionDefinition(Name)]
public sealed class P2PDefinition : ICollectionFixture<P2PFixture>
{
    public const string Name = "p2p-schema";
}

/// <summary>A clock a test can move.</summary>
public sealed class FakeClock : TimeProvider
{
    private DateTimeOffset _now;

    public FakeClock(DateTimeOffset now) => _now = now;

    public override DateTimeOffset GetUtcNow() => _now;

    public void Advance(TimeSpan by) => _now += by;
}

/// <summary>
/// A ledger that records what it was asked to post, and can be told to fail.
/// </summary>
/// <remarks>
/// <para>
/// A near-copy of the marketplace's, and deliberately not shared. Putting it
/// in the common test project would hand every module's tests a dependency on
/// Ledger.Contracts, which is the coupling the architecture rules exist to
/// prevent — the same reason the two modules repeat the settlement pattern
/// rather than inheriting it.
/// </para>
/// <para>
/// It enforces the one property the real ledger enforces and this module
/// depends on: an idempotency key may be used once. It also tracks balances,
/// because P2P asks the ledger what the settlement fund holds before it
/// promises anything.
/// </para>
/// </remarks>
public sealed class FakeLedger : ILedger
{
    private readonly Dictionary<string, Guid> _byKey = new(StringComparer.Ordinal);
    private readonly Dictionary<string, long> _balances = new(StringComparer.Ordinal);

    /// <summary>Every posting accepted, in order.</summary>
    public List<PostingRequest> Posted { get; } = [];

    /// <summary>Throws after recording the posting, as a crash would look.</summary>
    public bool FailAfterPosting { get; set; }

    /// <summary>Refuses the next posting for want of funds.</summary>
    public Money? RefuseWith { get; set; }

    /// <summary>Puts money somewhere without going through a posting.</summary>
    public void Fund(AccountRef account, Money amount)
    {
        ArgumentNullException.ThrowIfNull(account);
        _balances[account.ToString()] = _balances.GetValueOrDefault(account.ToString())
            + amount.MinorUnits;
    }

    public Task EnsureUserAccountsAsync(
        string userId, IReadOnlyCollection<Currency> currencies,
        CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task<IReadOnlyList<AccountBalance>> BalancesAsync(
        string userId, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<AccountBalance>>([]);

    public Task<Money> BalanceOfAsync(
        AccountRef account, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(account);
        return Task.FromResult(Money.FromMinorUnits(Balance(account), account.Currency));
    }

    public Task<LedgerEntryPage> EntriesAsync(
        string userId, int limit, string? cursor = null,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(new LedgerEntryPage([], null));

    public Task<Guid?> FindPostingAsync(
        string idempotencyKey, CancellationToken cancellationToken = default) =>
        Task.FromResult(_byKey.TryGetValue(idempotencyKey, out var id) ? id : (Guid?)null);

    public Task<PostingReceipt> PostAsync(
        PostingRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (RefuseWith is { } available)
        {
            RefuseWith = null;
            var wanted = request.Legs.First(l => l.Amount.IsNegative).Amount.Abs();
            throw new InsufficientFundsException(request.Legs[0].Account, available, wanted);
        }

        if (request.IdempotencyKey is { Length: > 0 } key && _byKey.TryGetValue(key, out var existing))
        {
            return Task.FromResult(new PostingReceipt(existing, Written: false));
        }

        // The real ledger refuses a posting whose legs do not sum to zero per
        // currency. Checking it here too means a wrong set of legs fails in
        // the module's own tests rather than only end to end.
        foreach (var group in request.Legs.GroupBy(l => l.Amount.Currency))
        {
            var sum = group.Sum(l => l.Amount.MinorUnits);
            if (sum != 0)
            {
                throw new InvalidOperationException(
                    $"The {group.Key.Code()} legs sum to {sum}, not zero.");
            }
        }

        if (request.Legs.Any(l => l.Amount.IsZero))
        {
            throw new InvalidOperationException("A zero leg would be refused by the ledger.");
        }

        var postingId = request.PostingId ?? Guid.NewGuid();
        if (request.IdempotencyKey is { Length: > 0 } fresh) _byKey[fresh] = postingId;

        Posted.Add(request);
        foreach (var leg in request.Legs)
        {
            var name = leg.Account.ToString();
            _balances[name] = _balances.GetValueOrDefault(name) + leg.Amount.MinorUnits;
        }

        if (FailAfterPosting)
        {
            FailAfterPosting = false;
            throw new InvalidOperationException("The ledger committed and the caller then died.");
        }

        return Task.FromResult(new PostingReceipt(postingId, Written: true));
    }

    /// <summary>The net of every leg that touched an account, in minor units.</summary>
    public long Balance(AccountRef account)
    {
        ArgumentNullException.ThrowIfNull(account);
        return _balances.GetValueOrDefault(account.ToString());
    }
}

/// <summary>A directory of people invented by a test.</summary>
public sealed class FakeDirectory : IUserDirectory
{
    private readonly Dictionary<string, DirectoryUser> _byId = new(StringComparer.Ordinal);

    public DirectoryUser Add(string id, string name = "Someone", bool phoneVerified = true)
    {
        var user = new DirectoryUser(id, $"{id}@example.test", name, phoneVerified);
        _byId[id] = user;
        return user;
    }

    public Task<DirectoryUser?> FindByIdAsync(
        string userId, CancellationToken cancellationToken = default) =>
        Task.FromResult(_byId.GetValueOrDefault(userId));

    public Task<DirectoryUser?> FindByEmailAsync(
        string email, CancellationToken cancellationToken = default) =>
        Task.FromResult(_byId.Values.FirstOrDefault(u =>
            string.Equals(u.Email, email, StringComparison.OrdinalIgnoreCase)));
}
