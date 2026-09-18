using System.Diagnostics;
using System.Text.Json;
using IslaPay.Contracts;
using IslaPay.Identity;
using Microsoft.AspNetCore.Http;

namespace IslaPay.Api;

/// <summary>
/// The one place a failure becomes a response body.
/// </summary>
/// <remarks>
/// Every error the API emits goes through here, so the shape the client parses
/// cannot drift between endpoints. ASP.NET's own
/// <c>ProblemDetails</c> is deliberately not used: it has no <c>code</c>, and
/// <c>code</c> is the only field the client is allowed to branch on.
/// </remarks>
public static class ProblemResults
{
    public const string ContentType = "application/problem+json";

    public static async Task WriteAsync(
        HttpContext context, string code, int status, string? detail = null,
        IReadOnlyDictionary<string, object>? meta = null)
    {
        ArgumentNullException.ThrowIfNull(context);

        var problem = new ApiProblem(
            Code: code,
            Title: TitleFor(status),
            Status: status,
            Detail: detail,
            Type: $"https://docs.islapay.cu/errors/{code}",
            Instance: context.Request.Path.Value,
            // The trace id, so a screenshot of an error in Havana and a log
            // line on the server can be tied together without guessing.
            CorrelationId: Activity.Current?.TraceId.ToString() ?? context.TraceIdentifier,
            Meta: meta);

        context.Response.StatusCode = status;
        context.Response.ContentType = ContentType;

        await context.Response.WriteAsync(
            JsonSerializer.Serialize(problem, IslaPayJson.Options),
            context.RequestAborted).ConfigureAwait(false);
    }

    public static Task WriteAsync(HttpContext context, IdentityException failure)
    {
        ArgumentNullException.ThrowIfNull(failure);

        return WriteAsync(
            context,
            failure.Code,
            failure.Status,
            // The provider's own words. Useful to a developer reading logs;
            // the client never shows it, because it is neither translated nor
            // written for a user.
            failure.Message,
            failure.Meta.Count > 0 ? failure.Meta : null);
    }

    private static string TitleFor(int status) => status switch
    {
        400 => "Bad request",
        401 => "Unauthorized",
        403 => "Forbidden",
        404 => "Not found",
        409 => "Conflict",
        422 => "Unprocessable content",
        429 => "Too many requests",
        503 => "Service unavailable",
        _ => "Error",
    };
}
