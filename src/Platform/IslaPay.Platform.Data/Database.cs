using System.Data;
using Npgsql;

namespace IslaPay.Platform.Data;

/// <summary>Opens connections and runs work inside a transaction.</summary>
/// <remarks>
/// <para>
/// Deliberately thin, and deliberately not an ORM. A ledger's correctness
/// lives in the isolation level and in which rows are locked, in what order —
/// facts that must be visible in the code that depends on them. An ORM hides
/// exactly those, and the failure mode is a lost update that only appears
/// under load.
/// </para>
/// <para>
/// So: hand-written SQL, explicit transactions, explicit <c>FOR UPDATE</c>.
/// There is less of it than an ORM would generate and all of it can be read.
/// </para>
/// </remarks>
public interface IDatabase
{
    Task<NpgsqlConnection> OpenAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Runs <paramref name="work"/> in one transaction, committing on return
    /// and rolling back on any exception.
    /// </summary>
    Task<T> InTransactionAsync<T>(
        Func<NpgsqlConnection, NpgsqlTransaction, CancellationToken, Task<T>> work,
        IsolationLevel isolation = IsolationLevel.ReadCommitted,
        CancellationToken cancellationToken = default);
}

/// <inheritdoc />
public sealed class Database : IDatabase, IAsyncDisposable
{
    private readonly NpgsqlDataSource _source;

    public Database(DataOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        // A data source, not a bare connection string: it owns the pool and
        // the type mappings, and creating one per call would open a new
        // physical connection for every request.
        _source = new NpgsqlDataSourceBuilder(options.ConnectionString).Build();
    }

    public async Task<NpgsqlConnection> OpenAsync(CancellationToken cancellationToken = default) =>
        await _source.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

    public async Task<T> InTransactionAsync<T>(
        Func<NpgsqlConnection, NpgsqlTransaction, CancellationToken, Task<T>> work,
        IsolationLevel isolation = IsolationLevel.ReadCommitted,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(work);

        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection
            .BeginTransactionAsync(isolation, cancellationToken).ConfigureAwait(false);

        var result = await work(connection, transaction, cancellationToken).ConfigureAwait(false);

        // Only on the way out, and only if nothing threw: a partial posting
        // committed because an exception was swallowed is the one failure a
        // ledger cannot recover from on its own.
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return result;
    }

    public ValueTask DisposeAsync() => _source.DisposeAsync();
}
