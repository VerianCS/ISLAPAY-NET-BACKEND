using System.Security.Claims;
using IslaPay.Catalog.Contracts;
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

        // Which asset can be deposited on which chain, from the catalogue and
        // not from a list in this file. A client that hard-codes the pairs is
        // a client that keeps offering a chain after it has been switched off.
        group.MapGet("/networks", (ICurrencyCatalog catalog, string? currency) =>
        {
            var pairs = currency is { Length: > 0 }
                ? catalog.NetworksFor(currency)
                : [.. catalog.Currencies
                    .Where(c => c.IsOnChain)
                    .SelectMany(c => catalog.NetworksFor(c.Code))];

            return TypedResults.Ok<IReadOnlyList<DepositNetworkDto>>(
            [
                .. pairs.Where(p => p.Enabled).Select(p => new DepositNetworkDto(
                    p.NetworkId,
                    p.NetworkName,
                    p.CurrencyCode,
                    p.Contract,
                    p.Confirmations,
                    p.MemoRequired)),
            ]);
        });

        // Asset first, then chain. A network alone no longer names a deposit:
        // one chain carries several assets, and which one the money is in is
        // the part that decides where it lands.
        group.MapGet("/addresses/{currency}/{network}", async (
            string currency, string network, ClaimsPrincipal caller,
            CustodyService custody, CancellationToken ct) =>
            TypedResults.Ok(await custody
                .AddressAsync(SubjectOf(caller), currency, network, ct)
                .ConfigureAwait(false)));

        group.MapGet("/deposits", async (
            ClaimsPrincipal caller, CustodyService custody, int? limit,
            CancellationToken ct) =>
            TypedResults.Ok(await custody
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
