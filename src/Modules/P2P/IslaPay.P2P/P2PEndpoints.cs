using System.Security.Claims;
using IslaPay.P2P.Contracts;
using IslaPay.Platform.Api;
using IslaPay.Platform.AspNet;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace IslaPay.P2P;

/// <summary>
/// The market's routes, and the operator's.
/// </summary>
/// <remarks>
/// Two audiences in one file because they are two halves of one flow: every
/// trade a customer opens is a task somebody has to finish, and splitting them
/// across files would hide that. What separates them is the authorisation
/// policy, not the folder.
/// </remarks>
public static class P2PEndpoints
{
    /// <summary>The policy an operator endpoint requires. Registered by the platform.</summary>
    public const string OperatorPolicy = "p2p-operator";

    public static IEndpointRouteBuilder MapP2P(this IEndpointRouteBuilder routes)
    {
        ArgumentNullException.ThrowIfNull(routes);

        MapMarket(routes);
        MapOperator(routes);
        return routes;
    }

    private static void MapMarket(IEndpointRouteBuilder routes)
    {
        var group = routes.MapGroup("/v1/p2p").WithTags("P2P").RequireAuthorization();

        group.MapGet("/methods", async (P2PService p2p, CancellationToken ct) =>
            TypedResults.Ok(await p2p.MethodsAsync(ct).ConfigureAwait(false)));

        // A quote costs nothing and commits to nothing, so it carries no
        // idempotency key: there is nothing a repeat could duplicate.
        group.MapPost("/quotes", async (
            P2PQuoteRequest request, P2PService p2p, CancellationToken ct) =>
            TypedResults.Ok(await p2p.QuoteAsync(request, ct).ConfigureAwait(false)));

        group.MapPost("/trades", async (
            P2PTradeRequest request, ClaimsPrincipal caller,
            P2PService p2p, CancellationToken ct) =>
            TypedResults.Ok(await p2p.OpenAsync(SubjectOf(caller), request, ct).ConfigureAwait(false)))
            .WithMetadata(new IdempotentAttribute());

        group.MapGet("/trades/{id:guid}", async (
            Guid id, ClaimsPrincipal caller, P2PService p2p, CancellationToken ct) =>
            TypedResults.Ok(await p2p.TradeAsync(SubjectOf(caller), id, ct).ConfigureAwait(false)));

        group.MapPost("/trades/{id:guid}/cancel", async (
            Guid id, ClaimsPrincipal caller, P2PService p2p, CancellationToken ct) =>
            TypedResults.Ok(await p2p.CancelAsync(SubjectOf(caller), id, ct).ConfigureAwait(false)))
            .WithMetadata(new IdempotentAttribute());

        routes.MapGet("/v1/me/p2p/trades", async (
            ClaimsPrincipal caller, int? limit, string? cursor,
            P2PService p2p, CancellationToken ct) =>
            TypedResults.Ok(await p2p
                .TradesOfAsync(SubjectOf(caller), limit ?? 20, cursor, ct).ConfigureAwait(false)))
            .RequireAuthorization().WithTags("P2P");
    }

    /// <summary>
    /// What a person settling trades needs.
    /// </summary>
    /// <remarks>
    /// The console's P2P desk is built on these. They came first, and on
    /// purpose: a sell that nobody can confirm sits in the queue for ever and
    /// the customer's money sits with it, so the endpoints were never
    /// optional even while the interface was.
    /// </remarks>
    private static void MapOperator(IEndpointRouteBuilder routes)
    {
        var group = routes.MapGroup("/v1/admin/p2p")
            .WithTags("P2P operations")
            .RequireAuthorization(OperatorPolicy);

        group.MapGet("/queue", async (int? limit, P2PService p2p, CancellationToken ct) =>
            TypedResults.Ok(await p2p.QueueAsync(limit ?? 50, ct).ConfigureAwait(false)));

        // The queue shows what is waiting; this finds everything else — a buy
        // that expired and was paid late, or the trade a customer is calling
        // about with the reference in hand.
        group.MapGet("/trades", async (
            string? reference, string? status, int? limit,
            P2PService p2p, CancellationToken ct) =>
            TypedResults.Ok(await p2p
                .SearchTradesAsync(reference, status, limit ?? 50, ct).ConfigureAwait(false)));

        // Sending somebody money twice is the failure this key prevents, and
        // an operator on a bad connection is exactly who would retry.
        group.MapPost("/trades/{id:guid}/paid", async (
            Guid id, P2PSettleRequest request, ClaimsPrincipal caller,
            P2PService p2p, CancellationToken ct) =>
            TypedResults.Ok(await p2p
                .ConfirmPayoutAsync(SubjectOf(caller), id, request.Reference, ct)
                .ConfigureAwait(false)))
            .WithMetadata(new IdempotentAttribute());

        group.MapPost("/trades/{id:guid}/received", async (
            Guid id, P2PSettleRequest request, ClaimsPrincipal caller,
            P2PService p2p, CancellationToken ct) =>
            TypedResults.Ok(await p2p
                .ConfirmReceiptAsync(SubjectOf(caller), id, request.Reference, ct)
                .ConfigureAwait(false)))
            .WithMetadata(new IdempotentAttribute());

        group.MapPost("/trades/{id:guid}/failed", async (
            Guid id, P2PFailRequest request, ClaimsPrincipal caller,
            P2PService p2p, CancellationToken ct) =>
            TypedResults.Ok(await p2p
                .FailPayoutAsync(SubjectOf(caller), id, request.Reason, ct)
                .ConfigureAwait(false)))
            .WithMetadata(new IdempotentAttribute());

        group.MapPut("/rates", async (
            P2PRateUpdate update, ClaimsPrincipal caller,
            P2PService p2p, CancellationToken ct) =>
        {
            await p2p.SetRateAsync(SubjectOf(caller), update, ct).ConfigureAwait(false);
            return TypedResults.NoContent();
        });

        group.MapGet("/methods", async (P2PService p2p, CancellationToken ct) =>
            TypedResults.Ok(await p2p.AdminMethodsAsync(ct).ConfigureAwait(false)));

        group.MapPost("/methods", async (
            P2PMethodCreate create, P2PService p2p, CancellationToken ct) =>
        {
            var created = await p2p.CreateMethodAsync(create, ct).ConfigureAwait(false);
            return TypedResults.Created($"/v1/admin/p2p/methods/{created.Id}", created);
        });

        group.MapPatch("/methods/{id}", async (
            string id, P2PMethodUpdate update, P2PService p2p, CancellationToken ct) =>
            TypedResults.Ok(await p2p.UpdateMethodAsync(id, update, ct).ConfigureAwait(false)));

        group.MapPut("/methods/{id}/available", async (
            string id, bool value, P2PService p2p, CancellationToken ct) =>
        {
            await p2p.SetAvailabilityAsync(id, value, ct).ConfigureAwait(false);
            return TypedResults.NoContent();
        });

        group.MapPut("/methods/{id}/instructions", async (
            string id, P2PInstructionsUpdate update, P2PService p2p, CancellationToken ct) =>
        {
            await p2p.SetInstructionsAsync(id, update.Instructions, ct).ConfigureAwait(false);
            return TypedResults.NoContent();
        });
    }

    /// <summary>
    /// Who is asking, taken from the validated token and never from the
    /// request.
    /// </summary>
    private static string SubjectOf(ClaimsPrincipal caller) =>
        caller.FindFirstValue(ClaimTypes.NameIdentifier)
        ?? caller.FindFirstValue("sub")
        ?? throw new P2PException(
            PlatformErrors.TokenInvalid, StatusCodes.Status401Unauthorized,
            "The token carries no subject.");
}
