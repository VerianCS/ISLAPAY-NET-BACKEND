using System.Data;
using System.Security.Claims;
using IslaPay.Catalog.Contracts;
using IslaPay.Platform;
using IslaPay.Platform.AspNet.Security;
using IslaPay.Platform.Data;
using IslaPay.Treasury.Contracts;
using Microsoft.AspNetCore.Http;
using Npgsql;

namespace IslaPay.Treasury;

/// <summary>
/// Four eyes on every movement of the house's money.
/// </summary>
/// <remarks>
/// <para>
/// A credit is proposed by one person and posted when a second approves it.
/// The proposer cannot approve their own, whatever roles they hold; the
/// approver cannot change what was proposed, only accept or refuse it. That is
/// the whole control, and it is why the posting's metadata carries both names.
/// </para>
/// <para>
/// Approval is safe to repeat. The ledger posting is keyed by the proposal, so
/// an approval that posted and then failed to record itself posts nothing the
/// second time and records what the first one did.
/// </para>
/// </remarks>
public sealed class TreasuryProposals
{
    /// <summary>How long a proposal waits for a decision.</summary>
    /// <remarks>
    /// A day. Long enough for the approver to be in a different shift, short
    /// enough that yesterday's figures are not approved against today's.
    /// </remarks>
    public static readonly TimeSpan Lifetime = TimeSpan.FromHours(24);

    private readonly IDatabase _database;
    private readonly TreasuryService _treasury;
    private readonly TreasuryIssuance _issuance;
    private readonly ICurrencyCatalog _catalog;
    private readonly IAuditLog _audit;
    private readonly TimeProvider _clock;

    public TreasuryProposals(
        IDatabase database, TreasuryService treasury, TreasuryIssuance issuance,
        ICurrencyCatalog catalog, IAuditLog audit, TimeProvider clock)
    {
        _database = database;
        _treasury = treasury;
        _issuance = issuance;
        _catalog = catalog;
        _audit = audit;
        _clock = clock;
    }

    /// <summary>Records a credit for somebody else to approve. Moves nothing.</summary>
    public Task<TreasuryProposalDto> ProposeCreditAsync(
        HttpContext context, CreditRequest request, string requestKey, CancellationToken ct = default)
    {
        var (destination, amount, source, reason) = _treasury.Validate(request);
        return ProposeAsync(context, "credit", destination, amount, source, reason, requestKey, ct);
    }

    /// <summary>E-ISLA from the issuer, once somebody else approves and the reserves cover it.</summary>
    public async Task<TreasuryProposalDto> ProposeMintAsync(
        HttpContext context, IssuanceRequest request, string requestKey, CancellationToken ct = default)
    {
        var (account, amount, reason) = _issuance.Validate(request);
        // Checked now for the proposer's sake, and again at approval, which is
        // the check that counts: reserves move in between.
        await _issuance.RequireCoveredAsync(amount, ct).ConfigureAwait(false);
        return await ProposeAsync(context, "mint", account, amount, "issuer", reason, requestKey, ct)
            .ConfigureAwait(false);
    }

    /// <summary>E-ISLA back to the issuer, once somebody else approves.</summary>
    public Task<TreasuryProposalDto> ProposeBurnAsync(
        HttpContext context, IssuanceRequest request, string requestKey, CancellationToken ct = default)
    {
        var (account, amount, reason) = _issuance.Validate(request);
        return ProposeAsync(context, "burn", "issuer", amount, account, reason, requestKey, ct);
    }

    private async Task<TreasuryProposalDto> ProposeAsync(
        HttpContext context, string kind, string destination, Money amount, string source, string reason,
        string requestKey, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);
        var (by, byName) = Who(context.User);
        var now = _clock.GetUtcNow();

        var proposal = await _database.InTransactionAsync(async (connection, transaction, token) =>
        {
            await using var insert = new NpgsqlCommand("""
                INSERT INTO treasury.proposals
                    (id, kind, status, destination, currency, amount_minor, source, reason,
                     proposed_by, proposed_by_name, request_key, proposed_at, expires_at)
                VALUES (@id, @kind, 'pending', @destination, @currency, @amount, @source, @reason,
                        @by, @byName, @key, @now, @expires)
                ON CONFLICT (proposed_by, request_key) DO NOTHING;
                """, connection, transaction);
            insert.Parameters.AddWithValue("id", Guid.NewGuid());
            insert.Parameters.AddWithValue("kind", kind);
            insert.Parameters.AddWithValue("destination", destination);
            insert.Parameters.AddWithValue("currency", amount.Currency.Code);
            insert.Parameters.AddWithValue("amount", amount.MinorUnits);
            insert.Parameters.AddWithValue("source", source);
            insert.Parameters.AddWithValue("reason", reason);
            insert.Parameters.AddWithValue("by", by);
            insert.Parameters.AddWithValue("byName", (object?)byName ?? DBNull.Value);
            insert.Parameters.AddWithValue("key", requestKey);
            insert.Parameters.AddWithValue("now", now);
            insert.Parameters.AddWithValue("expires", now + Lifetime);
            await insert.ExecuteNonQueryAsync(token).ConfigureAwait(false);

            // The row this key made, now or on an earlier try.
            await using var read = new NpgsqlCommand(
                $"{Select} WHERE proposed_by = @by AND request_key = @key;", connection, transaction);
            read.Parameters.AddWithValue("by", by);
            read.Parameters.AddWithValue("key", requestKey);
            return (await ReadAsync(read, token).ConfigureAwait(false)).Single();
        }, cancellationToken: ct).ConfigureAwait(false);

        await _audit.RecordAsync(context, Record($"treasury.{kind}.proposed", proposal), ct).ConfigureAwait(false);
        return proposal;
    }

    /// <summary>Proposals, newest first, optionally only those in one state.</summary>
    public async Task<IReadOnlyList<TreasuryProposalDto>> ListAsync(
        string? status, int limit, CancellationToken ct = default)
    {
        if (status is not null && !TreasuryProposalStatuses.All.Contains(status, StringComparer.Ordinal))
        {
            throw new TreasuryException(
                TreasuryErrors.InvalidCredit, StatusCodes.Status422UnprocessableEntity,
                $"'{status}' is not a proposal status; one of {string.Join(", ", TreasuryProposalStatuses.All)}.");
        }

        await ExpireAsync(ct).ConfigureAwait(false);

        await using var connection = await _database.OpenAsync(ct).ConfigureAwait(false);
        await using var read = new NpgsqlCommand(
            $"{Select} WHERE (@status IS NULL OR status = @status) ORDER BY proposed_at DESC LIMIT @limit;",
            connection);
        read.Parameters.Add(new NpgsqlParameter<string?>("status", NpgsqlTypes.NpgsqlDbType.Text) { TypedValue = status });
        read.Parameters.AddWithValue("limit", Math.Clamp(limit, 1, 200));
        return await ReadAsync(read, ct).ConfigureAwait(false);
    }

    public async Task<TreasuryProposalDto> GetAsync(Guid id, CancellationToken ct = default)
    {
        await ExpireAsync(ct).ConfigureAwait(false);
        await using var connection = await _database.OpenAsync(ct).ConfigureAwait(false);
        await using var read = new NpgsqlCommand($"{Select} WHERE id = @id;", connection);
        read.Parameters.AddWithValue("id", id);
        return (await ReadAsync(read, ct).ConfigureAwait(false)).SingleOrDefault()
            ?? throw NotFound(id);
    }

    /// <summary>Posts the proposal, if somebody other than its author asks.</summary>
    public async Task<TreasuryProposalDto> ApproveAsync(
        HttpContext context, Guid id, string? note, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        var (by, byName) = Who(context.User);

        var decided = await _database.InTransactionAsync(async (connection, transaction, token) =>
        {
            var proposal = await LockAsync(connection, transaction, id, token).ConfigureAwait(false);
            RequireDecidable(proposal, by);

            // Posted while the row is locked, so a second approver waits here
            // and then finds it approved. Keyed by the proposal: if this
            // transaction dies after the ledger committed, the next approval
            // posts nothing new and records the posting the first one made.
            Guid postingId;
            switch (proposal.Kind)
            {
                case "credit":
                    postingId = (await _treasury.CreditAsync(
                        proposal.ProposedBy,
                        new CreditRequest(proposal.Destination, proposal.Amount, proposal.Source, proposal.Reason),
                        $"proposal:{proposal.Id:N}",
                        approvedBy: by,
                        cancellationToken: token).ConfigureAwait(false)).PostingId;
                    break;

                case "mint" or "burn":
                    // One issuance at a time across all proposals, so two
                    // mints approved together cannot each see the headroom the
                    // other is about to use.
                    await using (var serialise = new NpgsqlCommand(
                        "SELECT pg_advisory_xact_lock(hashtext('treasury:issuance'));", connection, transaction))
                    {
                        await serialise.ExecuteNonQueryAsync(token).ConfigureAwait(false);
                    }

                    postingId = (proposal.Kind == "mint"
                        ? await _issuance.MintAsync(proposal, by, token).ConfigureAwait(false)
                        : await _issuance.BurnAsync(proposal, by, token).ConfigureAwait(false)).PostingId;
                    break;

                default:
                    throw new InvalidOperationException($"a proposal of kind '{proposal.Kind}'.");
            }

            return await DecideAsync(
                connection, transaction, id, TreasuryProposalStatuses.Approved, by, byName, note,
                postingId, token).ConfigureAwait(false);
        }, cancellationToken: ct).ConfigureAwait(false);

        await _audit.RecordAsync(context, Record($"treasury.{decided.Kind}.approved", decided), ct).ConfigureAwait(false);
        return decided;
    }

    /// <summary>Turns a proposal down. Nothing moves.</summary>
    public async Task<TreasuryProposalDto> RejectAsync(
        HttpContext context, Guid id, string? note, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        var (by, byName) = Who(context.User);
        var why = (note ?? string.Empty).Trim();
        if (why.Length < 4)
        {
            throw new TreasuryException(
                TreasuryErrors.InvalidCredit, StatusCodes.Status422UnprocessableEntity,
                "a rejection says why, so the proposer can fix it and propose again.");
        }

        var decided = await _database.InTransactionAsync(async (connection, transaction, token) =>
        {
            var proposal = await LockAsync(connection, transaction, id, token).ConfigureAwait(false);
            RequireDecidable(proposal, by);
            return await DecideAsync(
                connection, transaction, id, TreasuryProposalStatuses.Rejected, by, byName, why, null, token)
                .ConfigureAwait(false);
        }, cancellationToken: ct).ConfigureAwait(false);

        await _audit.RecordAsync(context, Record($"treasury.{decided.Kind}.rejected", decided), ct).ConfigureAwait(false);
        return decided;
    }

    /// <summary>The proposer takes it back before anybody decides.</summary>
    public async Task<TreasuryProposalDto> WithdrawAsync(
        HttpContext context, Guid id, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        var (by, byName) = Who(context.User);

        var decided = await _database.InTransactionAsync(async (connection, transaction, token) =>
        {
            var proposal = await LockAsync(connection, transaction, id, token).ConfigureAwait(false);
            RequirePending(proposal);
            if (!string.Equals(proposal.ProposedBy, by, StringComparison.Ordinal))
            {
                throw new TreasuryException(
                    TreasuryErrors.NotYourProposal, StatusCodes.Status403Forbidden,
                    "only whoever proposed this can withdraw it; to refuse it, reject it.");
            }

            return await DecideAsync(
                connection, transaction, id, TreasuryProposalStatuses.Withdrawn, by, byName, null, null, token)
                .ConfigureAwait(false);
        }, cancellationToken: ct).ConfigureAwait(false);

        await _audit.RecordAsync(context, Record($"treasury.{decided.Kind}.withdrawn", decided), ct).ConfigureAwait(false);
        return decided;
    }

    private static void RequireDecidable(TreasuryProposalDto proposal, string by)
    {
        RequirePending(proposal);
        if (string.Equals(proposal.ProposedBy, by, StringComparison.Ordinal))
        {
            throw new TreasuryException(
                TreasuryErrors.OwnProposal, StatusCodes.Status403Forbidden,
                "a proposal is decided by somebody other than whoever made it.");
        }
    }

    private static void RequirePending(TreasuryProposalDto proposal)
    {
        if (proposal.Status != TreasuryProposalStatuses.Pending)
        {
            throw new TreasuryException(
                TreasuryErrors.ProposalNotPending, StatusCodes.Status409Conflict,
                $"this proposal is {proposal.Status}.",
                new Dictionary<string, object>(StringComparer.Ordinal) { ["status"] = proposal.Status });
        }
    }

    /// <summary>The row, locked for this transaction, with its expiry applied.</summary>
    private async Task<TreasuryProposalDto> LockAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, Guid id, CancellationToken ct)
    {
        await using var read = new NpgsqlCommand($"{Select} WHERE id = @id FOR UPDATE;", connection, transaction);
        read.Parameters.AddWithValue("id", id);
        var proposal = (await ReadAsync(read, ct).ConfigureAwait(false)).SingleOrDefault()
            ?? throw NotFound(id);

        if (proposal.Status == TreasuryProposalStatuses.Pending && proposal.ExpiresAt <= _clock.GetUtcNow())
        {
            await using var expire = new NpgsqlCommand(
                "UPDATE treasury.proposals SET status = 'expired' WHERE id = @id;", connection, transaction);
            expire.Parameters.AddWithValue("id", id);
            await expire.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            return proposal with { Status = TreasuryProposalStatuses.Expired };
        }

        return proposal;
    }

    private async Task<TreasuryProposalDto> DecideAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, Guid id, string status,
        string by, string? byName, string? note, Guid? postingId, CancellationToken ct)
    {
        await using var update = new NpgsqlCommand("""
            UPDATE treasury.proposals
               SET status = @status, decided_by = @by, decided_by_name = @byName,
                   decided_at = @at, decision_note = @note, posting_id = @posting
             WHERE id = @id;
            """, connection, transaction);
        update.Parameters.AddWithValue("id", id);
        update.Parameters.AddWithValue("status", status);
        update.Parameters.AddWithValue("by", by);
        update.Parameters.AddWithValue("byName", (object?)byName ?? DBNull.Value);
        update.Parameters.AddWithValue("at", _clock.GetUtcNow());
        update.Parameters.AddWithValue("note", string.IsNullOrWhiteSpace(note) ? DBNull.Value : note.Trim());
        update.Parameters.AddWithValue("posting", (object?)postingId ?? DBNull.Value);
        await update.ExecuteNonQueryAsync(ct).ConfigureAwait(false);

        await using var read = new NpgsqlCommand($"{Select} WHERE id = @id;", connection, transaction);
        read.Parameters.AddWithValue("id", id);
        return (await ReadAsync(read, ct).ConfigureAwait(false)).Single();
    }

    /// <summary>Marks as expired whatever has waited too long, so lists say so.</summary>
    private async Task ExpireAsync(CancellationToken ct)
    {
        await using var connection = await _database.OpenAsync(ct).ConfigureAwait(false);
        await using var expire = new NpgsqlCommand(
            "UPDATE treasury.proposals SET status = 'expired' WHERE status = 'pending' AND expires_at <= @now;",
            connection);
        expire.Parameters.AddWithValue("now", _clock.GetUtcNow());
        await expire.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private const string Select = """
        SELECT id, kind, status, destination, currency, amount_minor, source, reason,
               proposed_by, proposed_by_name, proposed_at, expires_at,
               decided_by, decided_by_name, decided_at, decision_note, posting_id
          FROM treasury.proposals
        """;

    private async Task<List<TreasuryProposalDto>> ReadAsync(NpgsqlCommand command, CancellationToken ct)
    {
        var rows = new List<TreasuryProposalDto>();
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            // Describe, not Require: a proposal in a currency switched off
            // since is still something somebody has to be able to read.
            var code = reader.GetString(4);
            var currency = _catalog.Describe(code)?.Currency
                ?? throw new InvalidOperationException($"proposal in unknown currency {code}");
            rows.Add(new TreasuryProposalDto(
                reader.GetGuid(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                Money.FromMinorUnits(reader.GetInt64(5), currency),
                reader.GetString(6),
                reader.GetString(7),
                reader.GetString(8),
                reader.IsDBNull(9) ? null : reader.GetString(9),
                reader.GetFieldValue<DateTimeOffset>(10),
                reader.GetFieldValue<DateTimeOffset>(11),
                reader.IsDBNull(12) ? null : reader.GetString(12),
                reader.IsDBNull(13) ? null : reader.GetString(13),
                reader.IsDBNull(14) ? null : reader.GetFieldValue<DateTimeOffset>(14),
                reader.IsDBNull(15) ? null : reader.GetString(15),
                reader.IsDBNull(16) ? null : reader.GetGuid(16)));
        }

        return rows;
    }

    private static (string By, string? Name) Who(ClaimsPrincipal user) =>
        (user.FindFirstValue(ClaimTypes.NameIdentifier) ?? user.FindFirstValue("sub")
            ?? throw new TreasuryException(
                Platform.Api.PlatformErrors.TokenInvalid, StatusCodes.Status401Unauthorized,
                "The token carries no subject."),
         user.FindFirstValue("preferred_username") ?? user.FindFirstValue(ClaimTypes.Email));

    private static TreasuryException NotFound(Guid id) => new(
        TreasuryErrors.ProposalNotFound, StatusCodes.Status404NotFound,
        $"there is no proposal {id}.");

    private static AuditRecord Record(string action, TreasuryProposalDto proposal) => new(
        "treasury", action, "ok", Target: $"proposal:{proposal.Id}",
        Details: new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["kind"] = proposal.Kind,
            ["destination"] = proposal.Destination,
            ["amount"] = proposal.Amount.ToString(),
            ["currency"] = proposal.Amount.Currency.Code,
            ["source"] = proposal.Source,
            ["proposed_by"] = proposal.ProposedByName ?? proposal.ProposedBy,
            ["status"] = proposal.Status,
        });
}
