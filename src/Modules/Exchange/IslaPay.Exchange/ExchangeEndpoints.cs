using System.Security.Claims;
using IslaPay.Exchange.Contracts;
using IslaPay.Platform.Api;
using IslaPay.Platform.AspNet;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace IslaPay.Exchange;

/// <summary><c>/v1/exchange</c>: price a conversion, then make it.</summary>
public static class ExchangeEndpoints
{
    public static IEndpointRouteBuilder MapExchange(this IEndpointRouteBuilder routes)
    {
        ArgumentNullException.ThrowIfNull(routes);

        var group = routes.MapGroup("/v1/exchange").WithTags("Exchange").RequireAuthorization();

        // A quote commits to nothing, so it carries no key.
        group.MapPost("/quotes", async (
            QuoteRequest request, ClaimsPrincipal caller, ExchangeService exchange, CancellationToken ct) =>
            TypedResults.Ok(await exchange.QuoteAsync(SubjectOf(caller), request, ct).ConfigureAwait(false)));

        group.MapPost("/conversions", async (
            ConversionRequest request, ClaimsPrincipal caller, ExchangeService exchange, CancellationToken ct) =>
            TypedResults.Ok(await exchange.ConvertAsync(SubjectOf(caller), request, ct).ConfigureAwait(false)))
            .WithMetadata(new IdempotentAttribute());

        return routes;
    }

    private static string SubjectOf(ClaimsPrincipal caller) =>
        caller.FindFirstValue(ClaimTypes.NameIdentifier)
        ?? caller.FindFirstValue("sub")
        ?? throw new ExchangeException(
            PlatformErrors.TokenInvalid, StatusCodes.Status401Unauthorized, "The token carries no subject.");
}
