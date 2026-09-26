using System.Security.Claims;
using System.Text.Json;
using IslaPay.Platform.AspNet.Security;
using Microsoft.AspNetCore.Authentication;

namespace IslaPay.Identity;

/// <summary>
/// Puts Keycloak's realm roles and second factor where the platform looks.
/// </summary>
/// <remarks>
/// <para>
/// Keycloak writes realm roles into a <c>realm_access</c> claim whose value is
/// a JSON object, which the bearer handler leaves alone. Before this existed
/// each module that needed a role parsed that object itself — three copies of
/// the same twenty lines. The shape of a Keycloak token is this module's
/// knowledge, so this module is the one place that reads it.
/// </para>
/// <para>
/// The second factor arrives in <c>amr</c>, the methods the person
/// authenticated with, when the realm maps it. <c>otp</c> is what Keycloak
/// calls a code from an authenticator app.
/// </para>
/// </remarks>
internal sealed class RealmRoleClaims : IClaimsTransformation
{
    private const string Done = "islapay:roles-read";

    /// <summary>
    /// Where <c>amr</c> ends up. The bearer handler renames the registered JWT
    /// claims to their WS-* URIs unless told not to, and this one among them.
    /// </summary>
    private static readonly string[] AmrTypes =
        ["amr", "http://schemas.microsoft.com/claims/authnmethodsreferences"];

    private static readonly HashSet<string> SecondFactors =
        new(StringComparer.Ordinal) { "otp", "mfa", "hwk", "webauthn" };

    public Task<ClaimsPrincipal> TransformAsync(ClaimsPrincipal principal)
    {
        ArgumentNullException.ThrowIfNull(principal);

        // Runs every time something authenticates the request, which can be
        // more than once. The marker keeps the roles from being added twice.
        if (principal.Identity is not ClaimsIdentity { IsAuthenticated: true } identity
            || principal.HasClaim(c => c.Type == Done))
        {
            return Task.FromResult(principal);
        }

        var added = new ClaimsIdentity();
        added.AddClaim(new Claim(Done, "true"));

        foreach (var role in RealmRoles(principal.FindFirst("realm_access")?.Value))
        {
            if (!principal.IsInRole(role))
                added.AddClaim(new Claim(identity.RoleClaimType, role));
        }

        if (principal.Claims.Any(c => AmrTypes.Contains(c.Type) && IsSecondFactor(c)))
            added.AddClaim(new Claim(StaffClaims.MultiFactor, "true"));

        // Added to the authenticated identity, not as a second one, so the
        // identity's role claim type is the one IsInRole checks.
        identity.AddClaims(added.Claims);
        return Task.FromResult(principal);
    }

    private static bool IsSecondFactor(Claim amr)
    {
        // One claim holding a JSON array, or one claim per method: the bearer
        // handler produces either depending on how the token was written.
        if (SecondFactors.Contains(amr.Value)) return true;
        if (!amr.Value.StartsWith('[')) return false;

        try
        {
            using var document = JsonDocument.Parse(amr.Value);
            return document.RootElement.EnumerateArray().Any(e =>
                e.ValueKind == JsonValueKind.String && SecondFactors.Contains(e.GetString()!));
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>The roles in a <c>realm_access</c> value; none if it will not parse.</summary>
    internal static IReadOnlyList<string> RealmRoles(string? realmAccess)
    {
        if (string.IsNullOrWhiteSpace(realmAccess)) return [];

        try
        {
            using var document = JsonDocument.Parse(realmAccess);
            if (document.RootElement.ValueKind != JsonValueKind.Object
                || !document.RootElement.TryGetProperty("roles", out var roles)
                || roles.ValueKind != JsonValueKind.Array)
            {
                return [];
            }

            return
            [
                .. roles.EnumerateArray()
                    .Where(r => r.ValueKind == JsonValueKind.String)
                    .Select(r => r.GetString()!),
            ];
        }
        catch (JsonException)
        {
            // A claim that will not parse grants nothing. Refusing is the only
            // safe reading of something unreadable.
            return [];
        }
    }
}
