using System.Security.Claims;
using IslaPay.Contracts;
using IslaPay.Contracts.Identity;
using IslaPay.Identity;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace IslaPay.Api;

/// <summary>
/// <c>/v1/auth/*</c>, exactly the surface in <c>API_CONTRACT.md</c> §3.
/// </summary>
/// <remarks>
/// Thin on purpose. Every one of these is a translation between the contract's
/// shapes and <see cref="IdentityService"/>; none of them decides anything.
/// D8 warned that these routes are where password and token handling gets
/// quietly reinvented, so the rule here is that a change to what
/// authentication <em>means</em> belongs in the identity layer, and a change
/// to what it looks like on the wire belongs in the contracts.
/// </remarks>
public static class AuthEndpoints
{
    public static RouteGroupBuilder MapAuth(this IEndpointRouteBuilder routes)
    {
        ArgumentNullException.ThrowIfNull(routes);

        var group = routes.MapGroup("/v1/auth").WithTags("Identity");

        group.MapPost("/login", async (LoginRequest request, IdentityService identity, CancellationToken ct) =>
            Results.Ok(await identity.LoginAsync(request, ct).ConfigureAwait(false)))
            .AllowAnonymous();

        group.MapPost("/register", async (RegisterRequest request, IdentityService identity, CancellationToken ct) =>
            Results.Ok(await identity.RegisterAsync(request, ct).ConfigureAwait(false)))
            .AllowAnonymous();

        group.MapPost("/token/refresh", async (RefreshRequest request, IdentityService identity, CancellationToken ct) =>
            Results.Ok(await identity.RefreshAsync(request, ct).ConfigureAwait(false)))
            .AllowAnonymous();

        // Verify and resend need a token. The account is taken from it, never
        // from the body: an unauthenticated endpoint that marks a phone
        // verified hands that state to anyone who can read one SMS.
        group.MapPost("/otp/verify", async (
            OtpVerifyRequest request, ClaimsPrincipal caller, IdentityService identity, CancellationToken ct) =>
            Results.Ok(await identity.VerifyOtpAsync(SubjectOf(caller), request, ct).ConfigureAwait(false)))
            .RequireAuthorization();

        group.MapPost("/otp/resend", async (
            ClaimsPrincipal caller, IdentityService identity, CancellationToken ct) =>
            Results.Ok(await identity.ResendOtpAsync(SubjectOf(caller), ct).ConfigureAwait(false)))
            .RequireAuthorization();

        // One path, two steps, told apart by whether a code was sent. The
        // contract lists a single route and the client's screen is a single
        // screen; splitting it here would put a shape in the API that nothing
        // on the other side has.
        group.MapPost("/password/reset", async (
            PasswordResetConfirmRequest request, IdentityService identity, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(request.Code))
            {
                var sent = await identity
                    .RequestPasswordResetAsync(new PasswordResetRequest(request.Email), ct)
                    .ConfigureAwait(false);
                return Results.Accepted(value: sent);
            }

            await identity.ConfirmPasswordResetAsync(request, ct).ConfigureAwait(false);
            return Results.NoContent();
        }).AllowAnonymous();

        group.MapPost("/logout", async (RefreshRequest request, IdentityService identity, CancellationToken ct) =>
        {
            await identity.LogoutAsync(request.RefreshToken, ct).ConfigureAwait(false);
            return Results.NoContent();
        }).RequireAuthorization();

        return group;
    }

    /// <summary>
    /// The subject of a token the bearer middleware has already validated.
    /// </summary>
    /// <remarks>
    /// A principal that reached an authorized endpoint without a subject means
    /// the token or the realm is misconfigured, not that the caller did
    /// anything wrong — hence 401 rather than a crash.
    /// </remarks>
    private static string SubjectOf(ClaimsPrincipal caller) =>
        caller.FindFirstValue(ClaimTypes.NameIdentifier)
        ?? caller.FindFirstValue("sub")
        ?? throw IdentityException.TokenInvalid("The token carries no subject.");
}
