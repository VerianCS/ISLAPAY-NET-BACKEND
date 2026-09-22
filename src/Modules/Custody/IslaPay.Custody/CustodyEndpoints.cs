using System.Security.Claims;
using IslaPay.Custody.Contracts;
using IslaPay.Platform;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace IslaPay.Custody;

/// <summary>
/// <c>/v1/me/custody/*</c> — where to send money, and what has arrived.
/// </summary>
/// <remarks>
/// Read-only, all of it. Nothing a client can call makes a deposit happen:
/// deposits happen on a chain, and this module's write path belongs to the
/// scanner, which is not an HTTP caller. That asymmetry is worth noticing —
/// it is the first module in this system whose main input is not a request.
/// </remarks>
public static class CustodyEndpoints
{
    public static IEndpointRouteBuilder MapCustody(this IEndpointRouteBuilder routes)
    {
        ArgumentNullException.ThrowIfNull(routes);

        var group = routes.MapGroup("/v1/me/custody")
            .WithTags("Custody")
            .RequireAuthorization();

        // The networks this build watches, so a client does not hard-code a
        // list it cannot keep in step with the server's.
        group.MapGet("/networks", () => Results.Ok(
            CustodyNetworks.All.Select(n => new
            {
                id = n.Id,
                name = n.Name,
                currency = n.Currency.Code(),
                confirmations = n.Confirmations,
            })));

        group.MapGet("/addresses/{network}", async (
            string network, ClaimsPrincipal caller, CustodyService custody,
            CancellationToken ct) =>
            Results.Ok(await custody
                .AddressAsync(SubjectOf(caller), network, ct)
                .ConfigureAwait(false)));

        group.MapGet("/deposits", async (
            ClaimsPrincipal caller, CustodyService custody, int? limit,
            CancellationToken ct) =>
            Results.Ok(await custody
                .DepositsAsync(SubjectOf(caller), limit ?? 20, ct)
                .ConfigureAwait(false)));

        return routes;
    }

    /// <summary>
    /// The caller's id, from the token and nowhere else.
    /// </summary>
    /// <remarks>
    /// Never from a route or a body. An id a caller can choose is an id a
    /// caller can choose to be somebody else's — and here it would hand them
    /// another person's deposit address.
    /// </remarks>
    private static string SubjectOf(ClaimsPrincipal caller) =>
        caller.FindFirstValue(ClaimTypes.NameIdentifier)
        ?? caller.FindFirstValue("sub")
        ?? throw new CustodyException(
            IslaPay.Platform.Api.PlatformErrors.TokenInvalid,
            StatusCodes.Status401Unauthorized,
            "The token carries no subject.");
}
