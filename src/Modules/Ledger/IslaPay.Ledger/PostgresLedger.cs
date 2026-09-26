using System.Data;
using System.Globalization;
using System.Text;
using System.Text.Json;
using IslaPay.Catalog.Contracts;
using IslaPay.Ledger.Contracts;
using IslaPay.Ledger.Domain;
using IslaPay.Platform;
using IslaPay.Platform.Data;
using IslaPay.Platform.Messaging;
using Npgsql;
using NpgsqlTypes;

namespace IslaPay.Ledger;

/// <summary>
/// The ledger, on Postgres.
/// </summary>
/// <remarks>
/// <para>
/// The domain decides what is legal; this decides how it is stored, and the
/// two are kept apart on purpose. <see cref="Posting"/> already makes an
/// unbalanced transaction impossible to construct, so nothing here has to
/// check that it balances — which means nothing here can get that check
/// wrong.
/// </para>
/// <para>
/// What is genuinely this class's problem is concurrency. Two postings
/// touching the same account must not both read the same balance and both
/// decide there is enough. That is handled by locking the balance rows of
/// every account the posting touches, in a fixed order, before anything is
/// read — see <see cref="PostAsync"/>.
/// </para>
/// </remarks>
public sealed class PostgresLedger : ILedger
{
    private readonly IDatabase _database;
    private readonly IOutbox _outbox;
    private readonly ICurrencyCatalog _catalog;
    private readonly TimeProvider _clock;

    public PostgresLedger(
        IDatabase database,
        IOutbox outbox,
        ICurrencyCatalog catalog,
        TimeProvider? clock = null)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(outbox);
        ArgumentNullException.ThrowIfNull(catalog);
        _database = database;
        _outbox = outbox;
        _catalog = catalog;
        _clock = clock ?? TimeProvider.System;
    }

    /// <summary>
    /// What a stored code means, including for a currency since withdrawn.
    /// </summary>
    /// <remarks>
    /// Reading, not writing, so it asks <c>Describe</c> rather than
    /// <c>Require</c>: an entry posted in a currency that has since been
    /// switched off is still an entry, and a statement that threw rather than
    /// showing it would be worse than useless. A code the catalogue has never
    /// heard of is different — that is a row nothing can interpret, and
    /// guessing a scale for it would misstate the amount by orders of
    /// magnitude.
    /// </remarks>
    private Currency Stored(string code) =>
        _catalog.Describe(code)?.Currency
        ?? throw new UnknownCurrencyException(
            code, "it is stored in the ledger but no longer listed in catalog.currencies.");

    // ------------------------------------------------------------------ reads

    public async Task EnsureUserAccountsAsync(
        string userId,
        IReadOnlyCollection<Currency> currencies,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);
        ArgumentNullException.ThrowIfNull(currencies);

        // Checked before the transaction opens, and not only when money later
        // moves. An account is a promise that a balance in this currency can
        // exist; opening one in a currency the catalogue does not allow would
        // show the customer a wallet they can never use, and the refusal would
        // arrive at their first transfer instead of here.
        foreach (var currency in currencies) RequireListed(currency);

        await _database.InTransactionAsync(async (connection, transaction, ct) =>
        {
            foreach (var currency in currencies)
                await OpenAccountAsync(connection, transaction, AccountId.User(userId, currency), ct)
                    .ConfigureAwait(false);

            return 0;
        }, IsolationLevel.ReadCommitted, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<AccountBalance>> BalancesAsync(
        string userId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);

        await using var connection = await _database.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand("""
            SELECT a.currency, COALESCE(b.minor_units, 0)
            FROM ledger.accounts a
            LEFT JOIN ledger.balances b ON b.account_id = a.id
            WHERE a.owner_type = 'user' AND a.owner = @owner
            ORDER BY a.currency;
            """, connection);
        command.Parameters.AddWithValue("owner", userId);

        var balances = new List<AccountBalance>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var currency = Stored(reader.GetString(0));
            balances.Add(new AccountBalance(
                currency, Money.FromMinorUnits(reader.GetInt64(1), currency)));
        }

        return balances;
    }

    public async Task<Money> BalanceOfAsync(
        AccountRef account, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(account);

        var resolved = Resolve(account);

        await using var connection = await _database.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand("""
            SELECT COALESCE(b.minor_units, 0)
            FROM ledger.accounts a
            LEFT JOIN ledger.balances b ON b.account_id = a.id
            WHERE a.name = @name;
            """, connection);
        command.Parameters.AddWithValue("name", resolved.ToString());

        var result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);

        // No row at all means the account has never been opened, which holds
        // exactly as much as an open one nobody has posted to.
        return Money.FromMinorUnits(result is long minor ? minor : 0, account.Currency);
    }

    public async Task<IReadOnlyList<HouseBalance>> HouseBalancesAsync(
        CancellationToken cancellationToken = default)
    {
        await using var connection = await _database.OpenAsync(cancellationToken).ConfigureAwait(false);

        // An INNER JOIN, not a LEFT one. A balance row is written the moment
        // an account is opened, so the two sets are the same in practice and
        // the join is the cheaper way to say "and what is in it". The ordering
        // is what the screen wants: the platform's accounts, then the mirrors,
        // each grouped by name.
        await using var command = new NpgsqlCommand("""
            SELECT a.owner_type, a.owner, a.currency, b.minor_units, b.entry_count
            FROM ledger.accounts a
            JOIN ledger.balances b ON b.account_id = a.id
            WHERE a.owner_type IN ('platform', 'external')
            ORDER BY a.owner_type, a.owner, a.currency;
            """, connection);

        var balances = new List<HouseBalance>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var currency = Stored(reader.GetString(2));
            var account = Unresolve(reader.GetString(0), reader.GetString(1), currency);
            balances.Add(new HouseBalance(
                account,
                Money.FromMinorUnits(reader.GetInt64(3), currency),
                reader.GetInt64(4)));
        }

        return balances;
    }

    public async Task<LedgerEntryPage> AccountEntriesAsync(
        AccountRef account,
        int limit,
        string? cursor = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(account);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limit);

        var take = Math.Min(limit, 200) + 1;
        var after = DecodeCursor(cursor);

        await using var connection = await _database.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand("""
            SELECT e.id, p.kind, e.currency, e.minor_units, e.occurred_at, p.metadata
            FROM ledger.entries e
            JOIN ledger.accounts a ON a.id = e.account_id
            JOIN ledger.postings p ON p.id = e.posting_id
            WHERE a.name = @name
              AND (@after IS NULL OR e.id < @after)
            ORDER BY e.id DESC
            LIMIT @take;
            """, connection);
        command.Parameters.AddWithValue("name", Resolve(account).ToString());
        command.Parameters.Add(new NpgsqlParameter("after", NpgsqlDbType.Bigint)
        {
            Value = (object?)after ?? DBNull.Value,
        });
        command.Parameters.AddWithValue("take", take);

        var items = new List<LedgerEntryView>(take);
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false))
        {
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var currency = Stored(reader.GetString(2));
                items.Add(new LedgerEntryView(
                    Id: reader.GetInt64(0),
                    Kind: reader.GetString(1),
                    Amount: Money.FromMinorUnits(reader.GetInt64(3), currency),
                    OccurredAt: reader.GetFieldValue<DateTimeOffset>(4).ToUniversalTime(),
                    Metadata: ReadMetadata(reader.GetString(5))));
            }
        }

        string? next = null;
        if (items.Count == take)
        {
            items.RemoveAt(items.Count - 1);
            next = EncodeCursor(items[^1].Id);
        }

        return new LedgerEntryPage(items, next);
    }

    public async Task<LedgerEntryPage> EntriesAsync(
        string userId,
        int limit,
        string? cursor = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limit);

        // One more than asked for, so the presence of a next page is known
        // rather than guessed from a full page — a full page can be the last.
        var take = Math.Min(limit, 200) + 1;
        var after = DecodeCursor(cursor);

        await using var connection = await _database.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand("""
            SELECT e.id, p.kind, e.currency, e.minor_units, e.occurred_at, p.metadata
            FROM ledger.entries e
            JOIN ledger.accounts a ON a.id = e.account_id
            JOIN ledger.postings p ON p.id = e.posting_id
            WHERE a.owner_type = 'user' AND a.owner = @owner
              AND (@after IS NULL OR e.id < @after)
            ORDER BY e.id DESC
            LIMIT @take;
            """, connection);
        command.Parameters.AddWithValue("owner", userId);
        // Typed explicitly rather than AddWithValue: a null with no declared
        // type leaves Postgres unable to infer one, and the query fails with
        // "could not determine data type of parameter" — on the first page
        // only, which is the page every caller starts with.
        command.Parameters.Add(new NpgsqlParameter("after", NpgsqlDbType.Bigint)
        {
            Value = (object?)after ?? DBNull.Value,
        });
        command.Parameters.AddWithValue("take", take);

        var items = new List<LedgerEntryView>(take);
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false))
        {
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var currency = Stored(reader.GetString(2));
                items.Add(new LedgerEntryView(
                    Id: reader.GetInt64(0),
                    Kind: reader.GetString(1),
                    Amount: Money.FromMinorUnits(reader.GetInt64(3), currency),
                    OccurredAt: reader.GetFieldValue<DateTimeOffset>(4).ToUniversalTime(),
                    Metadata: ReadMetadata(reader.GetString(5))));
            }
        }

        string? next = null;
        if (items.Count == take)
        {
            items.RemoveAt(items.Count - 1);
            next = EncodeCursor(items[^1].Id);
        }

        return new LedgerEntryPage(items, next);
    }

    // ------------------------------------------------------------------ writes

    /// <inheritdoc />
    public async Task<PostingReceipt> PostAsync(
        PostingRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var references = new Dictionary<string, AccountRef>(StringComparer.Ordinal);
        var legs = new List<Leg>(request.Legs.Count);

        foreach (var leg in request.Legs)
        {
            RequireListed(leg.Amount.Currency);

            var account = Resolve(leg.Account);
            references[account.ToString()] = leg.Account;
            legs.Add(new Leg(account, leg.Amount));
        }

        // Constructed here, so an unbalanced request is refused by the domain
        // before this class opens a transaction.
        var posting = new Posting(
            request.PostingId ?? Guid.NewGuid(),
            ParseKind(request.Kind),
            legs,
            _clock.GetUtcNow(),
            request.IdempotencyKey,
            correlationId: null,
            request.Metadata);

        try
        {
            var (id, written) = await PostAsync(posting, request.Events, cancellationToken)
                .ConfigureAwait(false);
            return new PostingReceipt(id, written);
        }
        catch (Domain.InsufficientFundsException e)
        {
            // Translated at the boundary: a caller holds the contract's types
            // and must not have to catch the domain's.
            throw new Contracts.InsufficientFundsException(
                references[e.Account.ToString()], e.Available, e.Requested);
        }
    }

    /// <summary>
    /// Records a posting, or returns the one an earlier attempt recorded.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Not on <see cref="ILedger"/>: nothing outside this module needs to move
    /// money yet, and an interface published before it has a caller acquires a
    /// shape that fits nobody.
    /// </para>
    /// <para>
    /// The locking order is by account name, always. Two concurrent postings
    /// that touch the same pair of accounts in opposite orders would otherwise
    /// each hold what the other wants — a deadlock that only appears under the
    /// load you get after launch.
    /// </para>
    /// </remarks>
    /// <returns>The posting's id, and whether this call is the one that wrote it.</returns>
    public Task<(Guid Id, bool Written)> PostAsync(
        Posting posting,
        IReadOnlyList<PendingEvent>? events = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(posting);

        return _database.InTransactionAsync(async (connection, transaction, ct) =>
        {
            if (posting.IdempotencyKey is { Length: > 0 } key)
            {
                var existing = await ExistingPostingAsync(connection, transaction, key, ct)
                    .ConfigureAwait(false);

                // A replay. Returning the original rather than posting again is
                // the entire point of the key: the caller retried because it
                // did not hear us the first time, not because it wants a
                // second transfer.
                if (existing is { } id) return (id, false);
            }

            var accounts = new Dictionary<AccountId, long>();
            foreach (var account in posting.Accounts.OrderBy(a => a.ToString(), StringComparer.Ordinal))
            {
                accounts[account] = await OpenAccountAsync(connection, transaction, account, ct)
                    .ConfigureAwait(false);
            }

            // Locked in the same fixed order the accounts were resolved in.
            var state = await LockBalancesAsync(
                connection, transaction, accounts.Values.Order().ToList(), ct).ConfigureAwait(false);

            // Every projected balance is checked before a single row is
            // written, so a posting refused on its third leg leaves no trace of
            // the first two.
            foreach (var group in posting.Legs.GroupBy(l => l.Account))
            {
                var accountId = accounts[group.Key];
                var current = state[accountId].MinorUnits;
                var projected = current + group.Sum(l => l.Amount.MinorUnits);

                if (projected < 0 && !group.Key.MayGoNegative)
                {
                    throw new Domain.InsufficientFundsException(
                        group.Key,
                        Money.FromMinorUnits(current, group.Key.Currency),
                        Money.FromMinorUnits(-group.Sum(l => l.Amount.MinorUnits), group.Key.Currency));
                }
            }

            await InsertPostingAsync(connection, transaction, posting, ct).ConfigureAwait(false);

            foreach (var leg in posting.Legs)
            {
                var accountId = accounts[leg.Account];
                var next = state[accountId];

                await InsertEntryAsync(
                    connection, transaction, posting, leg, accountId, next.EntryCount + 1, ct)
                    .ConfigureAwait(false);

                state[accountId] = next with
                {
                    MinorUnits = next.MinorUnits + leg.Amount.MinorUnits,
                    EntryCount = next.EntryCount + 1,
                };
            }

            foreach (var (accountId, balance) in state)
            {
                await UpdateBalanceAsync(
                    connection, transaction, accountId, balance, ct).ConfigureAwait(false);
            }

            // In this transaction, not after it. An event enqueued afterwards
            // is a second write that can fail on its own, and an announced
            // transfer that did not happen is worse than one that happened
            // quietly.
            foreach (var pending in events ?? [])
            {
                await _outbox.EnqueueAsync(
                    connection, transaction,
                    pending.Context, pending.RoutingKey, pending.Payload,
                    correlationId: posting.Id.ToString(),
                    cancellationToken: ct).ConfigureAwait(false);
            }

            return (posting.Id, true);
        }, IsolationLevel.ReadCommitted, cancellationToken);
    }

    // ------------------------------------------------------------------ plumbing

    /// <summary>Turns a contract reference into the domain's account identity.</summary>
    /// <summary>
    /// Refuses a leg in a currency the catalogue will not have.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The outermost edge of the change that made currencies a table. The
    /// ledger is where every movement of money in this system ends up, so it
    /// is the one place that can guarantee the invariant: no entry is ever
    /// written in a currency nobody listed, or one that has been switched off.
    /// A check in each calling module would be six checks, and the one that
    /// was forgotten would be the one that mattered.
    /// </para>
    /// <para>
    /// The scale is checked too, and it is the subtler half. A code alone does
    /// not say what <c>1500000</c> is worth; a <see cref="Currency"/> carrying
    /// a scale the catalogue disagrees with would post an amount wrong by a
    /// factor of ten thousand and balance perfectly while doing it.
    /// </para>
    /// </remarks>
    private void RequireListed(Currency currency)
    {
        if (!currency.IsDefined)
        {
            throw new UnknownCurrencyException(
                null, "a posting leg carries no currency at all.");
        }

        var listed = _catalog.Require(currency.Code);
        if (listed.Scale != currency.Scale)
        {
            throw new UnknownCurrencyException(
                currency.Code,
                $"it is accounted in {listed.Scale} decimal places and the posting "
                + $"states {currency.Scale}.");
        }
    }

    /// <summary>
    /// A stored row, back into the name a caller outside the module uses.
    /// </summary>
    /// <remarks>
    /// The inverse of <see cref="Resolve"/>, and it exists because the house
    /// balances are read by asking the database what accounts there are rather
    /// than by being told. The platform account names are the chart of
    /// accounts' own (<c>float</c>, not <c>cash_float</c>); an unrecognised one
    /// throws rather than being passed through, because a name this method
    /// does not know is a chart entry somebody added without telling the code
    /// that reads it.
    /// </remarks>
    private static AccountRef Unresolve(string ownerType, string owner, Currency currency) =>
        (ownerType, owner) switch
        {
            ("external", _) => AccountRef.External(owner, currency),
            ("platform", "fees") => AccountRef.Fees(currency),
            ("platform", "settlement_fund") => AccountRef.SettlementFund(currency),
            ("platform", "escrow") => AccountRef.Escrow(currency),
            ("platform", "float") => AccountRef.CashFloat(currency),
            ("platform", "issuer") => AccountRef.Issuer(currency),
            _ => throw new InvalidOperationException(
                $"ledger.accounts holds '{ownerType}:{owner}', which the contract has no name for."),
        };

    private static AccountId Resolve(AccountRef account) => account.Owner switch
    {
        AccountOwner.User => AccountId.User(account.Id, account.Currency),
        AccountOwner.Merchant => AccountId.Merchant(account.Id, account.Currency),
        AccountOwner.Fees => AccountId.Fees(account.Currency),
        AccountOwner.SettlementFund => AccountId.SettlementFund(account.Currency),
        AccountOwner.Escrow => AccountId.Escrow(account.Currency),
        AccountOwner.CashFloat => AccountId.CashFloat(account.Currency),
        AccountOwner.External => AccountId.External(account.Id, account.Currency),
        AccountOwner.Issuer => AccountId.Issuer(account.Currency),
        _ => throw new ArgumentOutOfRangeException(
            nameof(account), account.Owner, "Unclassified account owner."),
    };

    /// <summary>
    /// The ledger's own classification, from the string a caller sent.
    /// </summary>
    /// <remarks>
    /// Rejected rather than defaulted when unknown. A posting filed under the
    /// wrong kind is invisible to the report that was supposed to find it, and
    /// a silent fallback is how that happens.
    /// </remarks>
    private static TransactionKind ParseKind(string kind) =>
        Enum.TryParse<TransactionKind>(kind, ignoreCase: true, out var parsed)
            ? parsed
            : throw new ArgumentException(
                $"'{kind}' is not a transaction kind. Expected one of: "
                + string.Join(", ", Enum.GetNames<TransactionKind>()).ToLowerInvariant(),
                nameof(kind));

    private readonly record struct BalanceState(long MinorUnits, long EntryCount);

    /// <summary>
    /// The account's id, opening it first if it does not exist.
    /// </summary>
    /// <remarks>
    /// Reads before it writes, and the order matters for more than speed. The
    /// obvious shape — always <c>INSERT … ON CONFLICT DO UPDATE</c> — takes a
    /// row lock on the account every time, which silently serialises postings
    /// and makes the explicit lock in <see cref="PostAsync"/> look
    /// unnecessary. It is not unnecessary; it was merely being shadowed. An
    /// account is opened once and read for the rest of its life, so the common
    /// path is a plain SELECT that locks nothing, and the concurrency this
    /// class actually relies on is the one written down.
    /// </remarks>
    private static async Task<long> OpenAccountAsync(
        NpgsqlConnection connection, NpgsqlTransaction? transaction,
        AccountId account, CancellationToken cancellationToken)
    {
        await using (var existing = new NpgsqlCommand(
            "SELECT id FROM ledger.accounts WHERE name = @name;", connection, transaction))
        {
            existing.Parameters.AddWithValue("name", account.ToString());
            if (await existing.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false)
                is long found)
            {
                return found;
            }
        }

        // First time. ON CONFLICT DO UPDATE rather than DO NOTHING: DO NOTHING
        // returns no row, so a concurrent opener would have to re-select.
        // Assigning the name to itself is a no-op that still returns the id.
        await using var command = new NpgsqlCommand("""
            INSERT INTO ledger.accounts
                (name, owner_type, owner, currency, account_type, may_go_negative)
            VALUES (@name, @owner_type, @owner, @currency, @account_type, @may_go_negative)
            ON CONFLICT (name) DO UPDATE SET name = EXCLUDED.name
            RETURNING id;
            """, connection, transaction);

        command.Parameters.AddWithValue("name", account.ToString());
        command.Parameters.AddWithValue("owner_type", account.OwnerType.ToString().ToLowerInvariant());
        command.Parameters.AddWithValue("owner", account.Owner);
        command.Parameters.AddWithValue("currency", account.Currency.Code);
        command.Parameters.AddWithValue("account_type", account.Type.ToString().ToLowerInvariant());
        command.Parameters.AddWithValue("may_go_negative", account.MayGoNegative);

        var id = (long)(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!;

        await using var balance = new NpgsqlCommand("""
            INSERT INTO ledger.balances (account_id, minor_units, entry_count)
            VALUES (@id, 0, 0)
            ON CONFLICT (account_id) DO NOTHING;
            """, connection, transaction);
        balance.Parameters.AddWithValue("id", id);
        await balance.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

        return id;
    }

    private static async Task<Dictionary<long, BalanceState>> LockBalancesAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction,
        IReadOnlyList<long> accountIds, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("""
            SELECT account_id, minor_units, entry_count
            FROM ledger.balances
            WHERE account_id = ANY(@ids)
            ORDER BY account_id
            FOR UPDATE;
            """, connection, transaction);
        command.Parameters.Add(new NpgsqlParameter("ids", NpgsqlDbType.Array | NpgsqlDbType.Bigint)
        {
            Value = accountIds.ToArray(),
        });

        var state = new Dictionary<long, BalanceState>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            state[reader.GetInt64(0)] = new BalanceState(reader.GetInt64(1), reader.GetInt64(2));

        return state;
    }

    public Task<Guid?> FindPostingAsync(
        string idempotencyKey, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(idempotencyKey);

        // A transaction for a single read, so it goes through exactly the same
        // statement the replay check uses. Two ways to ask the same question
        // is two ways for the answers to drift apart.
        return _database.InTransactionAsync(
            (connection, transaction, ct) =>
                ExistingPostingAsync(connection, transaction, idempotencyKey, ct),
            cancellationToken: cancellationToken);
    }

    private static async Task<Guid?> ExistingPostingAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction,
        string idempotencyKey, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            "SELECT id FROM ledger.postings WHERE idempotency_key = @key;",
            connection, transaction);
        command.Parameters.AddWithValue("key", idempotencyKey);

        var result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return result is Guid id ? id : null;
    }

    private static async Task InsertPostingAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction,
        Posting posting, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("""
            INSERT INTO ledger.postings
                (id, kind, posted_at, idempotency_key, correlation_id, metadata)
            VALUES (@id, @kind, @posted_at, @key, @correlation, @metadata::jsonb);
            """, connection, transaction);

        command.Parameters.AddWithValue("id", posting.Id);
        command.Parameters.AddWithValue("kind", posting.Kind.ToString().ToLowerInvariant());
        command.Parameters.AddWithValue("posted_at", posting.PostedAt);
        command.Parameters.AddWithValue("key", (object?)posting.IdempotencyKey ?? DBNull.Value);
        command.Parameters.AddWithValue("correlation", (object?)posting.CorrelationId ?? DBNull.Value);
        command.Parameters.AddWithValue("metadata", JsonSerializer.Serialize(posting.Metadata));

        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task InsertEntryAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction,
        Posting posting, Leg leg, long accountId, long seq, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("""
            INSERT INTO ledger.entries
                (posting_id, account_id, seq, currency, minor_units, occurred_at)
            VALUES (@posting, @account, @seq, @currency, @minor_units, @occurred_at);
            """, connection, transaction);

        command.Parameters.AddWithValue("posting", posting.Id);
        command.Parameters.AddWithValue("account", accountId);
        command.Parameters.AddWithValue("seq", seq);
        command.Parameters.AddWithValue("currency", leg.Amount.Currency.Code);
        command.Parameters.AddWithValue("minor_units", leg.Amount.MinorUnits);
        command.Parameters.AddWithValue("occurred_at", posting.PostedAt);

        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task UpdateBalanceAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction,
        long accountId, BalanceState balance, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("""
            UPDATE ledger.balances
            SET minor_units = @minor_units, entry_count = @entry_count, updated_at = now()
            WHERE account_id = @id;
            """, connection, transaction);

        command.Parameters.AddWithValue("id", accountId);
        command.Parameters.AddWithValue("minor_units", balance.MinorUnits);
        command.Parameters.AddWithValue("entry_count", balance.EntryCount);

        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static Dictionary<string, string> ReadMetadata(string json)
    {
        var parsed = JsonSerializer.Deserialize<Dictionary<string, string>>(json);
        return parsed ?? new Dictionary<string, string>(StringComparer.Ordinal);
    }

    /// <summary>
    /// The cursor is the last entry id, encoded so it does not look parseable.
    /// </summary>
    /// <remarks>
    /// Opaque on purpose: a client that learns to construct one has taken a
    /// dependency on the ordering, and the ordering is ours to change.
    /// </remarks>
    private static string EncodeCursor(long id) =>
        Convert.ToBase64String(Encoding.UTF8.GetBytes(
            id.ToString(CultureInfo.InvariantCulture)));

    private static long? DecodeCursor(string? cursor)
    {
        if (string.IsNullOrWhiteSpace(cursor)) return null;

        try
        {
            var text = Encoding.UTF8.GetString(Convert.FromBase64String(cursor));
            return long.TryParse(text, CultureInfo.InvariantCulture, out var id) ? id : null;
        }
        catch (FormatException)
        {
            // A cursor we did not issue. Starting from the beginning is a
            // better answer than a 500 for what is almost always a stale link.
            return null;
        }
    }
}
