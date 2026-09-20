using System.Security.Claims;
using System.Text;
using IslaPay.Platform.Api;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace IslaPay.Platform.AspNet;

/// <summary>
/// Makes a retried request answer the same thing instead of happening twice.
/// </summary>
/// <remarks>
/// <para>
/// Middleware rather than an endpoint filter, and that is forced: a minimal
/// API filter returns the result before the framework executes it, so there is
/// no response body to capture at that point. Here the endpoint and its result
/// both run inside <c>next</c>.
/// </para>
/// <para>
/// A request that fails in a way that says nothing about whether it would fail
/// again — a 5xx, a crash — releases its claim so the same key can be retried.
/// A 4xx is kept, because the client asking the same wrong thing twice should
/// get the same answer without the server doing the work twice.
/// </para>
/// </remarks>
public sealed partial class IdempotencyMiddleware
{
    public const string HeaderName = "Idempotency-Key";

    private readonly RequestDelegate _next;
    private readonly IdempotencyStore _store;
    private readonly ILogger<IdempotencyMiddleware> _log;

    public IdempotencyMiddleware(
        RequestDelegate next, IdempotencyStore store, ILogger<IdempotencyMiddleware> log)
    {
        ArgumentNullException.ThrowIfNull(next);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(log);
        _next = next;
        _store = store;
        _log = log;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (context.GetEndpoint()?.Metadata.GetMetadata<IdempotentAttribute>() is null)
        {
            await _next(context);
            return;
        }

        var key = context.Request.Headers[HeaderName].ToString().Trim();
        if (key.Length == 0)
        {
            await ProblemResults.WriteAsync(
                context, PlatformErrors.MalformedRequest, StatusCodes.Status400BadRequest,
                $"This endpoint requires an {HeaderName} header: one value per user intent, "
                + "reused on every retry of that same intent.");
            return;
        }

        // Scoped to the caller, so two users choosing the same key cannot be
        // served each other's answers.
        var scope = context.User.FindFirstValue(ClaimTypes.NameIdentifier)
            ?? context.User.FindFirstValue("sub")
            ?? "anonymous";

        var body = await ReadBodyAsync(context);
        var endpoint = $"{context.Request.Method} {context.Request.Path}";
        var fingerprint = IdempotencyStore.Fingerprint(
            context.Request.Method, context.Request.Path, body);

        var outcome = await _store.ClaimAsync(
            scope, key, endpoint, fingerprint, context.RequestAborted);

        switch (outcome)
        {
            case IdempotencyOutcome.Replay replay:
                Replayed(_log, key, endpoint);
                context.Response.StatusCode = replay.Status;
                context.Response.ContentType = replay.Body is null
                    ? context.Response.ContentType
                    : "application/json";
                if (replay.Body is not null)
                    await context.Response.WriteAsync(replay.Body, context.RequestAborted);
                return;

            case IdempotencyOutcome.InFlight:
                // The client did not hear us and asked again while the first
                // attempt is still running. Retry-After, not an error the user
                // should see.
                context.Response.Headers.RetryAfter = "1";
                await ProblemResults.WriteAsync(
                    context, PlatformErrors.RequestInFlight, StatusCodes.Status409Conflict,
                    "A request with this key is still running.");
                return;

            case IdempotencyOutcome.Reused:
                await ProblemResults.WriteAsync(
                    context, PlatformErrors.IdempotencyKeyReuse,
                    StatusCodes.Status422UnprocessableEntity,
                    $"This {HeaderName} was used for a different request. "
                    + "Retrying will not help; use a new key for a new intent.");
                return;
        }

        await RunAndRecordAsync(context, scope, key);
    }

    private async Task RunAndRecordAsync(HttpContext context, string scope, string key)
    {
        var original = context.Response.Body;
        using var captured = new MemoryStream();
        context.Response.Body = captured;

        try
        {
            await _next(context);
        }
        catch
        {
            // Nothing is known about whether this would fail again, so the key
            // stays usable. The exception middleware upstream writes the
            // response after the body stream is restored below.
            await _store.ReleaseAsync(scope, key, CancellationToken.None);
            throw;
        }
        finally
        {
            captured.Position = 0;
            await captured.CopyToAsync(original, context.RequestAborted);
            context.Response.Body = original;
        }

        var status = context.Response.StatusCode;
        if (status >= StatusCodes.Status500InternalServerError)
        {
            await _store.ReleaseAsync(scope, key, CancellationToken.None);
            return;
        }

        captured.Position = 0;
        var text = Encoding.UTF8.GetString(captured.ToArray());
        await _store.CompleteAsync(scope, key, status, text.Length == 0 ? null : text,
            CancellationToken.None);
    }

    /// <summary>Reads the body and rewinds it so the endpoint can read it too.</summary>
    private static async Task<byte[]> ReadBodyAsync(HttpContext context)
    {
        context.Request.EnableBuffering();

        using var buffer = new MemoryStream();
        await context.Request.Body.CopyToAsync(buffer, context.RequestAborted);
        context.Request.Body.Position = 0;

        return buffer.ToArray();
    }

    [LoggerMessage(EventId = 1, Level = LogLevel.Information,
        Message = "Replayed {key} for {endpoint} instead of running it again.")]
    private static partial void Replayed(ILogger logger, string key, string endpoint);
}
