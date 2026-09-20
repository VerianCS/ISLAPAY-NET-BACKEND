using System.Security.Claims;
using IslaPay.Marketplace.Contracts;
using IslaPay.Platform.Api;
using IslaPay.Platform.AspNet;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace IslaPay.Marketplace;

/// <summary>
/// The marketplace's routes.
/// </summary>
/// <remarks>
/// Three of these move money and all three carry
/// <see cref="IdempotentAttribute"/>: placing an order, redeeming a code and
/// cancelling. A retry on a dropped connection must not lock a second hold,
/// pay a seller twice, or refund an order the retry then cancels again.
/// </remarks>
public static class MarketplaceEndpoints
{
    public static IEndpointRouteBuilder MapMarketplace(this IEndpointRouteBuilder routes)
    {
        ArgumentNullException.ThrowIfNull(routes);

        MapListings(routes);
        MapOrders(routes);
        return routes;
    }

    private static void MapListings(IEndpointRouteBuilder routes)
    {
        var listings = routes.MapGroup("/v1/listings")
            .WithTags("Marketplace").RequireAuthorization();

        listings.MapPost("/", async (
            PublishListingRequest request, ClaimsPrincipal caller,
            MarketplaceService marketplace, CancellationToken ct) =>
            Results.Ok(await marketplace
                .PublishAsync(SubjectOf(caller), request, ct).ConfigureAwait(false)));

        listings.MapGet("/", async (
            string? category, string? q, int? limit, string? cursor,
            MarketplaceService marketplace, CancellationToken ct) =>
            Results.Ok(await marketplace
                .BrowseAsync(category, q, limit ?? 20, cursor, ct).ConfigureAwait(false)));

        listings.MapGet("/{id:guid}", async (
            Guid id, MarketplaceService marketplace, CancellationToken ct) =>
            Results.Ok(await marketplace.ListingAsync(id, ct).ConfigureAwait(false)));

        // The seller takes it down. Not a DELETE: the row stays, because
        // orders reference it and a sold listing is part of somebody's
        // history.
        listings.MapPost("/{id:guid}/withdraw", async (
            Guid id, ClaimsPrincipal caller,
            MarketplaceService marketplace, CancellationToken ct) =>
            Results.Ok(await marketplace
                .WithdrawAsync(SubjectOf(caller), id, ct).ConfigureAwait(false)));

        routes.MapGet("/v1/me/listings", async (
            ClaimsPrincipal caller, int? limit, string? cursor,
            MarketplaceService marketplace, CancellationToken ct) =>
            Results.Ok(await marketplace
                .ListingsOfAsync(SubjectOf(caller), limit ?? 20, cursor, ct).ConfigureAwait(false)))
            .RequireAuthorization().WithTags("Marketplace");
    }

    private static void MapOrders(IEndpointRouteBuilder routes)
    {
        var orders = routes.MapGroup("/v1/orders")
            .WithTags("Marketplace").RequireAuthorization();

        orders.MapPost("/", async (
            PlaceOrderRequest request, ClaimsPrincipal caller,
            MarketplaceService marketplace, CancellationToken ct) =>
            Results.Ok(await marketplace
                .PlaceOrderAsync(SubjectOf(caller), request.ListingId, ct).ConfigureAwait(false)))
            .WithMetadata(new IdempotentAttribute());

        // No order id in the path: the code is the whole address. One scan,
        // one request, and a seller holding a phone does not have to have
        // opened the sale first.
        orders.MapPost("/redeem", async (
            RedeemOrderRequest request, ClaimsPrincipal caller,
            MarketplaceService marketplace, CancellationToken ct) =>
            Results.Ok(await marketplace
                .RedeemAsync(SubjectOf(caller), request.Code, ct).ConfigureAwait(false)))
            .WithMetadata(new IdempotentAttribute());

        orders.MapPost("/{id:guid}/cancel", async (
            Guid id, ClaimsPrincipal caller,
            MarketplaceService marketplace, CancellationToken ct) =>
            Results.Ok(await marketplace
                .CancelAsync(SubjectOf(caller), id, ct).ConfigureAwait(false)))
            .WithMetadata(new IdempotentAttribute());

        orders.MapGet("/{id:guid}", async (
            Guid id, ClaimsPrincipal caller,
            MarketplaceService marketplace, CancellationToken ct) =>
            Results.Ok(await marketplace
                .OrderAsync(SubjectOf(caller), id, ct).ConfigureAwait(false)));

        // Two lists, because they are two screens: what I am buying, and what
        // somebody is coming to collect from me.
        routes.MapGet("/v1/me/orders", async (
            ClaimsPrincipal caller, string? role, int? limit, string? cursor,
            MarketplaceService marketplace, CancellationToken ct) =>
            Results.Ok(await marketplace.OrdersOfAsync(
                SubjectOf(caller),
                asSeller: string.Equals(role, "seller", StringComparison.OrdinalIgnoreCase),
                limit ?? 20, cursor, ct).ConfigureAwait(false)))
            .RequireAuthorization().WithTags("Marketplace");
    }

    /// <summary>
    /// Who is asking, taken from the validated token and never from the
    /// request.
    /// </summary>
    /// <remarks>
    /// There is no user id in any path or query above, deliberately — the same
    /// rule Wallet follows. The check that cannot be forgotten is the one
    /// there is no parameter for.
    /// </remarks>
    private static string SubjectOf(ClaimsPrincipal caller) =>
        caller.FindFirstValue(ClaimTypes.NameIdentifier)
        ?? caller.FindFirstValue("sub")
        ?? throw new MarketplaceException(
            PlatformErrors.TokenInvalid, StatusCodes.Status401Unauthorized,
            "The token carries no subject.");
}
