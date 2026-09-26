using System.Globalization;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using IslaPay.Platform.Api;
using IslaPay.Platform.Data;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Npgsql;
using NpgsqlTypes;

namespace IslaPay.Platform.AspNet.Security;

/// <summary>Something a member of staff did, or tried to.</summary>
/// <param name="Kind"><c>route</c>, <c>denied</c>, or a module's own word.</param>
/// <param name="Action">What was done, e.g. <c>POST /v1/admin/p2p/rates</c> or <c>treasury.proposal.approved</c>.</param>
/// <param name="Outcome"><c>ok</c>, <c>refused</c>, <c>failed</c> or <c>denied</c>.</param>
public sealed record AuditRecord(
    string Kind,
    string Action,
    string Outcome,
    string? Permission = null,
    string? Target = null,
    int? Status = null,
    IReadOnlyDictionary<string, string>? Details = null);

/// <summary>One row of the audit log, as <c>GET /v1/admin/audit</c> returns it.</summary>
public sealed record AuditEntryDto(
    long Seq,
    DateTimeOffset At,
    string Actor,
    string? ActorName,
    string Kind,
    string Action,
    string? Permission,
    string? Target,
    string Outcome,
    int? Status,
    IReadOnlyDictionary<string, string> Details,
    string? Correlation,
    string Hash);

/// <summary>Writes the audit log. Nothing reads it back except the auditor's route.</summary>
public interface IAuditLog
{
    /// <summary>Appends a record for the caller of <paramref name="context"/>.</summary>
    Task RecordAsync(HttpContext context, AuditRecord record, CancellationToken cancellationToken = default);
}

/// <summary>
/// An append-only, hash-chained log in <c>platform.audit_log</c>.
/// </summary>
/// <remarks>
/// <para>
/// One writer at a time, by an advisory lock, because each row's hash covers
/// the previous row's. Staff actions are a handful a minute; serialising them
/// costs nothing, and a chain with a fork in it proves nothing.
/// </para>
/// <para>
/// A failure to write is logged and swallowed. By the time the record is
/// written the action has happened, and answering the operator with an error
/// for something that succeeded would get it retried.
/// </para>
/// </remarks>
public sealed partial class AuditLog : IAuditLog
{
    private const long ChainLock = 0x4155_4449_54; // "AUDIT"

    private readonly IDatabase _database;
    private readonly TimeProvider _clock;
    private readonly ILogger<AuditLog> _logger;

    public AuditLog(IDatabase database, TimeProvider clock, ILogger<AuditLog> logger)
    {
        _database = database;
        _clock = clock;
        _logger = logger;
    }

    public async Task RecordAsync(
        HttpContext context, AuditRecord record, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(record);

        var actor = context.User.FindFirstValue(ClaimTypes.NameIdentifier)
            ?? context.User.FindFirstValue("sub")
            ?? "anonymous";
        var actorName = context.User.FindFirstValue("preferred_username")
            ?? context.User.FindFirstValue(ClaimTypes.Email)
            ?? context.User.FindFirstValue("email");
        var correlation = System.Diagnostics.Activity.Current?.TraceId.ToString() ?? context.TraceIdentifier;
        var details = JsonSerializer.Serialize(
            record.Details ?? new Dictionary<string, string>(StringComparer.Ordinal));
        var at = _clock.GetUtcNow();

        try
        {
            await _database.InTransactionAsync(async (connection, transaction, ct) =>
            {
                await using (var lockChain = new NpgsqlCommand(
                    "SELECT pg_advisory_xact_lock(@key);", connection, transaction))
                {
                    lockChain.Parameters.AddWithValue("key", ChainLock);
                    await lockChain.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                }

                string previous;
                await using (var last = new NpgsqlCommand(
                    "SELECT hash FROM platform.audit_log ORDER BY seq DESC LIMIT 1;", connection, transaction))
                {
                    previous = await last.ExecuteScalarAsync(ct).ConfigureAwait(false) as string ?? "genesis";
                }

                var hash = Hash(previous, at, actor, record, details);

                await using var insert = new NpgsqlCommand("""
                    INSERT INTO platform.audit_log
                        (at, actor, actor_name, kind, action, permission, target, outcome,
                         status, details, correlation, prev_hash, hash)
                    VALUES (@at, @actor, @actorName, @kind, @action, @permission, @target, @outcome,
                            @status, @details, @correlation, @prev, @hash);
                    """, connection, transaction);
                insert.Parameters.AddWithValue("at", at);
                insert.Parameters.AddWithValue("actor", actor);
                insert.Parameters.AddWithValue("actorName", (object?)actorName ?? DBNull.Value);
                insert.Parameters.AddWithValue("kind", record.Kind);
                insert.Parameters.AddWithValue("action", record.Action);
                insert.Parameters.AddWithValue("permission", (object?)record.Permission ?? DBNull.Value);
                insert.Parameters.AddWithValue("target", (object?)record.Target ?? DBNull.Value);
                insert.Parameters.AddWithValue("outcome", record.Outcome);
                insert.Parameters.AddWithValue("status", (object?)record.Status ?? DBNull.Value);
                insert.Parameters.AddWithValue("details", NpgsqlDbType.Jsonb, details);
                insert.Parameters.AddWithValue("correlation", correlation);
                insert.Parameters.AddWithValue("prev", previous);
                insert.Parameters.AddWithValue("hash", hash);
                await insert.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                return 0;
            }, cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch (Exception e) when (e is NpgsqlException or InvalidOperationException or TimeoutException)
        {
            NotWritten(_logger, e, record.Kind, record.Action, actor, record.Outcome);
        }
    }

    [LoggerMessage(EventId = 1, Level = LogLevel.Error,
        Message = "Audit record not written: {Kind} {Action} by {Actor} ({Outcome})")]
    private static partial void NotWritten(
        ILogger logger, Exception exception, string kind, string action, string actor, string outcome);

    /// <summary>The chain link: this row's content and the previous row's hash.</summary>
    public static string Hash(string previous, DateTimeOffset at, string actor, AuditRecord record, string details)
    {
        ArgumentNullException.ThrowIfNull(record);
        var content = string.Join('\u001f',
            previous,
            at.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss.ffffffZ", CultureInfo.InvariantCulture),
            actor, record.Kind, record.Action, record.Permission ?? "", record.Target ?? "",
            record.Outcome, record.Status?.ToString(CultureInfo.InvariantCulture) ?? "", details);
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(content)));
    }

    /// <summary><c>GET /v1/admin/audit</c>, newest first.</summary>
    internal static void MapAudit(IEndpointRouteBuilder routes) =>
        routes.MapGet("/v1/admin/audit", async (
                string? actor, string? action, int? limit, string? cursor,
                IDatabase database, CancellationToken ct) =>
            {
                var size = Math.Clamp(limit ?? 50, 1, 200);
                long? before = long.TryParse(cursor, NumberStyles.None, CultureInfo.InvariantCulture, out var c)
                    ? c : null;

                await using var connection = await database.OpenAsync(ct).ConfigureAwait(false);
                await using var read = new NpgsqlCommand("""
                    SELECT seq, at, actor, actor_name, kind, action, permission, target, outcome,
                           status, details::text, correlation, hash
                      FROM platform.audit_log
                     WHERE (@before IS NULL OR seq < @before)
                       AND (@actor IS NULL OR actor = @actor OR actor_name = @actor)
                       AND (@action IS NULL OR action ILIKE '%' || @action || '%')
                     ORDER BY seq DESC
                     LIMIT @limit;
                    """, connection);
                var who = string.IsNullOrWhiteSpace(actor) ? null : actor.Trim();
                var what = string.IsNullOrWhiteSpace(action) ? null : action.Trim();
                read.Parameters.Add(new NpgsqlParameter<long?>("before", NpgsqlDbType.Bigint) { TypedValue = before });
                read.Parameters.Add(new NpgsqlParameter<string?>("actor", NpgsqlDbType.Text) { TypedValue = who });
                read.Parameters.Add(new NpgsqlParameter<string?>("action", NpgsqlDbType.Text) { TypedValue = what });
                read.Parameters.AddWithValue("limit", size + 1);

                var rows = new List<AuditEntryDto>();
                await using var reader = await read.ExecuteReaderAsync(ct).ConfigureAwait(false);
                while (await reader.ReadAsync(ct).ConfigureAwait(false))
                {
                    rows.Add(new AuditEntryDto(
                        reader.GetInt64(0),
                        reader.GetFieldValue<DateTimeOffset>(1),
                        reader.GetString(2),
                        reader.IsDBNull(3) ? null : reader.GetString(3),
                        reader.GetString(4),
                        reader.GetString(5),
                        reader.IsDBNull(6) ? null : reader.GetString(6),
                        reader.IsDBNull(7) ? null : reader.GetString(7),
                        reader.GetString(8),
                        reader.IsDBNull(9) ? null : reader.GetInt32(9),
                        JsonSerializer.Deserialize<Dictionary<string, string>>(reader.GetString(10)) ?? [],
                        reader.IsDBNull(11) ? null : reader.GetString(11),
                        reader.GetString(12)));
                }

                var next = rows.Count > size
                    ? rows[size - 1].Seq.ToString(CultureInfo.InvariantCulture)
                    : null;
                return TypedResults.Ok(new CursorPage<AuditEntryDto>([.. rows.Take(size)], next));
            })
            .RequirePermission(Permissions.AuditRead)
            .WithTags("Security");
}

/// <summary>
/// Records every non-GET call to a route behind a permission, however it ends.
/// </summary>
/// <remarks>
/// Attached by <see cref="PermissionAuthorization.RequirePermission{TBuilder}"/>,
/// so a route cannot be protected without being audited: the two are the same
/// line of code.
/// </remarks>
internal sealed class AuditFilter(string permission) : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var http = context.HttpContext;
        if (HttpMethods.IsGet(http.Request.Method) || HttpMethods.IsHead(http.Request.Method))
            return await next(context).ConfigureAwait(false);

        var action = $"{http.Request.Method} {(http.GetEndpoint() as RouteEndpoint)?.RoutePattern.RawText ?? http.Request.Path}";
        var target = http.Request.Path.Value;
        var details = http.Request.RouteValues
            .Where(v => v.Value is not null)
            .ToDictionary(v => v.Key, v => Convert.ToString(v.Value, CultureInfo.InvariantCulture) ?? "",
                StringComparer.Ordinal);
        foreach (var (key, value) in http.Request.Query)
            details[$"?{key}"] = value.ToString();

        var audit = http.RequestServices.GetRequiredService<IAuditLog>();
        try
        {
            var result = await next(context).ConfigureAwait(false);
            var status = (result as IStatusCodeHttpResult)?.StatusCode ?? StatusCodes.Status200OK;
            await audit.RecordAsync(http, new AuditRecord(
                "route", action, status < 400 ? "ok" : "refused", permission, target, status, details),
                http.RequestAborted).ConfigureAwait(false);
            return result;
        }
        catch (Exception e) when (e is IApiFailure failure)
        {
            details["code"] = failure.Code;
            await audit.RecordAsync(http, new AuditRecord(
                "route", action, "refused", permission, target, failure.Status, details),
                http.RequestAborted).ConfigureAwait(false);
            throw;
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            await audit.RecordAsync(http, new AuditRecord(
                "route", action, "failed", permission, target, StatusCodes.Status500InternalServerError, details),
                CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }
}
