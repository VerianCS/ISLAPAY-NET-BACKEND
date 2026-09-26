using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace IslaPay.Platform.AspNet.Security;

/// <summary>How strict staff sign-in is. Read from the <c>Security</c> section.</summary>
public sealed class StaffSecurityOptions
{
    /// <summary>
    /// Whether a staff role counts only when the sign-in had a second factor.
    /// </summary>
    /// <remarks>
    /// Off by default so a development machine and the test suite can sign
    /// in with a password; on in every environment that holds real money,
    /// where the host's configuration sets it.
    /// </remarks>
    public bool RequireMultiFactor { get; init; }
}

/// <summary>A route's declaration of the one permission it needs.</summary>
/// <remarks>
/// Metadata as well as a policy so something other than the authorisation
/// middleware can read it: the test that every admin route is covered, and the
/// OpenAPI document that tells the console what to expect.
/// </remarks>
public sealed record PermissionMetadata(string Permission);

internal sealed record PermissionRequirement(string Permission) : IAuthorizationRequirement;

internal sealed class PermissionHandler(StaffSecurityOptions options)
    : AuthorizationHandler<PermissionRequirement>
{
    protected override Task HandleRequirementAsync(
        AuthorizationHandlerContext context, PermissionRequirement requirement)
    {
        var access = StaffRoles.AccessOf(context.User, options.RequireMultiFactor);
        if (access.Permissions.Contains(requirement.Permission, StringComparer.Ordinal))
            context.Succeed(requirement);
        return Task.CompletedTask;
    }
}

public static class PermissionAuthorization
{
    /// <summary>One policy per permission, and the handler that decides them.</summary>
    internal static void AddPermissionPolicies(this IServiceCollection services, StaffSecurityOptions options)
    {
        services.AddSingleton(options);
        services.AddSingleton<IAuthorizationHandler, PermissionHandler>();
        services.AddSingleton<IAuditLog, AuditLog>();

        var builder = services.AddAuthorizationBuilder();
        foreach (var permission in Permissions.All)
        {
            builder.AddPolicy(Permissions.PolicyFor(permission), policy => policy
                .RequireAuthenticatedUser()
                .AddRequirements(new PermissionRequirement(permission)));
        }
    }

    /// <summary>
    /// Requires a signed-in caller holding <paramref name="permission"/>, and
    /// records in the audit log every call that is not a read.
    /// </summary>
    public static TBuilder RequirePermission<TBuilder>(this TBuilder builder, string permission)
        where TBuilder : IEndpointConventionBuilder
    {
        ArgumentNullException.ThrowIfNull(builder);
        if (!Permissions.All.Contains(permission, StringComparer.Ordinal))
        {
            // At start-up, where a typo is cheap, rather than as a 500 on the
            // first request to the route.
            throw new ArgumentException($"'{permission}' is not a permission.", nameof(permission));
        }

        return builder
            .RequireAuthorization(Permissions.PolicyFor(permission))
            .WithMetadata(new PermissionMetadata(permission))
            .AddEndpointFilter(new AuditFilter(permission));
    }

    /// <summary>
    /// <c>GET /v1/me/permissions</c>: what the console should offer this person.
    /// </summary>
    /// <remarks>
    /// The console draws its navigation from this rather than from role names
    /// it would otherwise have to keep in step with the table above. It is a
    /// convenience and nothing more: every route checks for itself.
    /// </remarks>
    internal static void MapStaffAccess(IEndpointRouteBuilder routes)
    {
        routes.MapGet("/v1/me/permissions", (ClaimsPrincipal caller, StaffSecurityOptions options) =>
                TypedResults.Ok(StaffRoles.AccessOf(caller, options.RequireMultiFactor)))
            .RequireAuthorization()
            .WithTags("Security");

        AuditLog.MapAudit(routes);
    }

    /// <summary>
    /// Records a refusal of a route that needs a permission.
    /// </summary>
    /// <remarks>
    /// A customer's token probing <c>/v1/admin</c>, or a member of staff
    /// reaching past their role, is exactly what somebody reviewing the log
    /// wants to see — and the one thing the route itself never hears about,
    /// because it never runs.
    /// </remarks>
    internal static async Task RecordDenialAsync(HttpContext context)
    {
        var permission = context.GetEndpoint()?.Metadata.GetMetadata<PermissionMetadata>();
        if (permission is null) return;

        var audit = context.RequestServices.GetRequiredService<IAuditLog>();
        var action = $"{context.Request.Method} "
            + $"{(context.GetEndpoint() as RouteEndpoint)?.RoutePattern.RawText ?? context.Request.Path}";
        await audit.RecordAsync(context, new AuditRecord(
            "denied", action, "denied", permission.Permission, context.Request.Path.Value,
            StatusCodes.Status403Forbidden), context.RequestAborted).ConfigureAwait(false);
    }
}
