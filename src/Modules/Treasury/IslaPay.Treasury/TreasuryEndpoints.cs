using IslaPay.Platform.Api;
using IslaPay.Platform.AspNet;
using IslaPay.Platform.AspNet.Security;
using IslaPay.Treasury.Contracts;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace IslaPay.Treasury;

/// <summary>
/// The console's routes into the platform's own money.
/// </summary>
/// <remarks>
/// <para>
/// Every route here is under <c>/v1/admin/treasury</c>. Reading needs
/// <see cref="Permissions.TreasuryRead"/>; the one route that moves money
/// needs <see cref="Permissions.TreasuryPropose"/> to be asked for and
/// <see cref="Permissions.TreasuryApprove"/>, held by somebody else, to happen.
/// None of them is reachable by a customer's token, and none of them takes a user id: this module answers questions about the house, and a route that
/// could also read a person's history would be a way to read anybody's with a
/// role granted for something else.
/// </para>
/// <para>
/// One route moves money and it carries <see cref="IdempotentAttribute"/>. It
/// has to: a credit retried on a dropped connection would otherwise fund the
/// float twice, and the second one looks exactly like the first in every
/// report.
/// </para>
/// </remarks>
public static class TreasuryEndpoints
{
    public static IEndpointRouteBuilder MapTreasury(this IEndpointRouteBuilder routes)
    {
        ArgumentNullException.ThrowIfNull(routes);

        var admin = routes.MapGroup("/v1/admin/treasury")
            .WithTags("Treasury")
            .RequireAuthorization();

        admin.MapGet("/balances", async (TreasuryService treasury, CancellationToken ct) =>
            TypedResults.Ok(await treasury.BalancesAsync(ct).ConfigureAwait(false)))
            .RequirePermission(Permissions.TreasuryRead);

        admin.MapGet("/reconciliation", async (TreasuryService treasury, CancellationToken ct) =>
            TypedResults.Ok(await treasury.ReconciliationAsync(ct).ConfigureAwait(false)))
            .RequirePermission(Permissions.TreasuryRead);

        // The mirror accounts are reached as external:tron, external:bank:bandec.
        // A colon in a path segment is legal and needs no escaping, which keeps
        // the URL readable by the person who has to paste it into a ticket.
        admin.MapGet("/accounts/{owner}/{currency}/entries", async (
            string owner, string currency, int? limit, string? cursor,
            TreasuryService treasury, CancellationToken ct) =>
            TypedResults.Ok(await treasury
                .EntriesAsync(owner, currency, limit ?? 50, cursor, ct).ConfigureAwait(false)))
            .RequirePermission(Permissions.TreasuryRead);

        // Proposes; moves nothing. The money moves when somebody else
        // approves, below. Created, not OK: what exists now is a proposal.
        admin.MapPost("/credits", async (
            CreditRequest request, HttpContext context,
            TreasuryProposals proposals, CancellationToken ct) =>
        {
            var proposal = await proposals
                .ProposeCreditAsync(context, request, KeyOf(context), ct).ConfigureAwait(false);
            return TypedResults.Created($"/v1/admin/treasury/proposals/{proposal.Id}", proposal);
        })
            .RequirePermission(Permissions.TreasuryPropose)
            .WithMetadata(new IdempotentAttribute());

        // E-ISLA: what is out, what backs it, and the two ways to change the
        // first — each a proposal, like a credit.
        admin.MapGet("/issuance", async (TreasuryIssuance issuance, CancellationToken ct) =>
            TypedResults.Ok(await issuance.ReportAsync(ct).ConfigureAwait(false)))
            .RequirePermission(Permissions.TreasuryRead);

        admin.MapPost("/mints", async (
            IssuanceRequest request, HttpContext context,
            TreasuryProposals proposals, CancellationToken ct) =>
        {
            var proposal = await proposals
                .ProposeMintAsync(context, request, KeyOf(context), ct).ConfigureAwait(false);
            return TypedResults.Created($"/v1/admin/treasury/proposals/{proposal.Id}", proposal);
        })
            .RequirePermission(Permissions.TreasuryPropose)
            .WithMetadata(new IdempotentAttribute());

        admin.MapPost("/burns", async (
            IssuanceRequest request, HttpContext context,
            TreasuryProposals proposals, CancellationToken ct) =>
        {
            var proposal = await proposals
                .ProposeBurnAsync(context, request, KeyOf(context), ct).ConfigureAwait(false);
            return TypedResults.Created($"/v1/admin/treasury/proposals/{proposal.Id}", proposal);
        })
            .RequirePermission(Permissions.TreasuryPropose)
            .WithMetadata(new IdempotentAttribute());

        admin.MapGet("/proposals", async (
            string? status, int? limit, TreasuryProposals proposals, CancellationToken ct) =>
            TypedResults.Ok(await proposals.ListAsync(status, limit ?? 50, ct).ConfigureAwait(false)))
            .RequirePermission(Permissions.TreasuryRead);

        admin.MapGet("/proposals/{id:guid}", async (
            Guid id, TreasuryProposals proposals, CancellationToken ct) =>
            TypedResults.Ok(await proposals.GetAsync(id, ct).ConfigureAwait(false)))
            .RequirePermission(Permissions.TreasuryRead);

        // Idempotent like the credit it posts: an approver on a bad connection
        // is exactly who presses twice.
        admin.MapPost("/proposals/{id:guid}/approve", async (
            Guid id, TreasuryDecisionRequest? decision, HttpContext context,
            TreasuryProposals proposals, CancellationToken ct) =>
            TypedResults.Ok(await proposals
                .ApproveAsync(context, id, decision?.Note, ct).ConfigureAwait(false)))
            .RequirePermission(Permissions.TreasuryApprove)
            .WithMetadata(new IdempotentAttribute());

        admin.MapPost("/proposals/{id:guid}/reject", async (
            Guid id, TreasuryDecisionRequest decision, HttpContext context,
            TreasuryProposals proposals, CancellationToken ct) =>
            TypedResults.Ok(await proposals
                .RejectAsync(context, id, decision.Note, ct).ConfigureAwait(false)))
            .RequirePermission(Permissions.TreasuryApprove);

        admin.MapPost("/proposals/{id:guid}/withdraw", async (
            Guid id, HttpContext context, TreasuryProposals proposals, CancellationToken ct) =>
            TypedResults.Ok(await proposals.WithdrawAsync(context, id, ct).ConfigureAwait(false)))
            .RequirePermission(Permissions.TreasuryPropose);

        return routes;
    }

    /// <summary>
    /// The key the caller sent, which the middleware has already required.
    /// </summary>
    /// <remarks>
    /// Read again here rather than trusted to be there. The endpoint's
    /// correctness must not depend on a piece of metadata elsewhere still
    /// being attached — somebody removing the attribute should break the
    /// credit loudly, not turn every retry into a second posting.
    /// </remarks>
    private static string KeyOf(HttpContext context) =>
        context.Request.Headers["Idempotency-Key"].ToString() is { Length: > 0 } key
            ? key
            : throw new TreasuryException(
                PlatformErrors.MalformedRequest, StatusCodes.Status400BadRequest,
                "A credit requires an Idempotency-Key header.");
}
