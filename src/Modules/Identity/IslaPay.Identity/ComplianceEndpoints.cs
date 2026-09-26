using IslaPay.Identity.Contracts;
using IslaPay.Platform.AspNet.Security;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace IslaPay.Identity;

/// <summary>
/// <c>/v1/admin/compliance</c>: look an account up, freeze it, set its level.
/// </summary>
/// <remarks>
/// Looking up needs <see cref="Permissions.SupportRead"/>, because support is
/// who takes the call; changing anything needs
/// <see cref="Permissions.ComplianceAct"/>. Every change carries a reason and
/// lands in the audit log with it.
/// </remarks>
public static class ComplianceEndpoints
{
    public static IEndpointRouteBuilder MapCompliance(this IEndpointRouteBuilder routes)
    {
        ArgumentNullException.ThrowIfNull(routes);

        var group = routes.MapGroup("/v1/admin/compliance/accounts")
            .WithTags("Compliance")
            .RequireAuthorization();

        group.MapGet("", async (string email, ComplianceService compliance, CancellationToken ct) =>
            TypedResults.Ok(await compliance.FindAsync(email, ct).ConfigureAwait(false)))
            .RequirePermission(Permissions.SupportRead);

        group.MapGet("/{id}", async (string id, ComplianceService compliance, CancellationToken ct) =>
            TypedResults.Ok(await compliance.GetAsync(id, ct).ConfigureAwait(false)))
            .RequirePermission(Permissions.SupportRead);

        group.MapPost("/{id}/freeze", async (
            string id, StandingChangeRequest request, HttpContext context,
            ComplianceService compliance, CancellationToken ct) =>
            TypedResults.Ok(await compliance.FreezeAsync(context, id, request, ct).ConfigureAwait(false)))
            .RequirePermission(Permissions.ComplianceAct);

        group.MapPost("/{id}/unfreeze", async (
            string id, StandingChangeRequest request, HttpContext context,
            ComplianceService compliance, CancellationToken ct) =>
            TypedResults.Ok(await compliance.UnfreezeAsync(context, id, request, ct).ConfigureAwait(false)))
            .RequirePermission(Permissions.ComplianceAct);

        group.MapPut("/{id}/level", async (
            string id, LevelChangeRequest request, HttpContext context,
            ComplianceService compliance, CancellationToken ct) =>
            TypedResults.Ok(await compliance.SetLevelAsync(context, id, request, ct).ConfigureAwait(false)))
            .RequirePermission(Permissions.ComplianceAct);

        return routes;
    }
}
