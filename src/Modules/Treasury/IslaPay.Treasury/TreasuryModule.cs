using System.Text.Json;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using IslaPay.Platform.AspNet;

namespace IslaPay.Treasury;

/// <summary>
/// The platform's own position, and the door money enters by.
/// </summary>
/// <remarks>
/// <para>
/// The only module with no schema of its own, and that is the design rather
/// than an omission. A treasury that kept its own figures would be a second
/// set of books, and the second set is always the one that is wrong. It reads
/// the ledger, asks each context with escrow what it is holding, and writes
/// exactly one kind of posting.
/// </para>
/// <para>
/// It is registered last, after every module that implements
/// <see cref="IEscrowReporter"/>, though nothing depends on that: the
/// reporters are resolved per request, so the order modules are composed in
/// does not change what the reconciliation sees.
/// </para>
/// </remarks>
public sealed class TreasuryModule : IIslaPayModule
{
    /// <summary>
    /// The realm role that may read the platform's money and add to it.
    /// </summary>
    /// <remarks>
    /// Its own role, not <c>catalog-admin</c> and not the P2P operator's. The
    /// three answer different questions — what currencies exist, whether a
    /// trade was paid, and where the company's money is — and the third is the
    /// one whose holder can raise the float. Rolling them together would mean
    /// the person who switches a currency on can also fund it.
    /// </remarks>
    public const string AdminRole = "treasury-admin";

    public string Name => "treasury";

    public void AddServices(IHostApplicationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.Services.AddScoped<TreasuryService>();

        builder.Services.AddAuthorizationBuilder()
            .AddPolicy(TreasuryEndpoints.AdminPolicy, policy =>
                policy.RequireAssertion(context => HasAdminRole(context.User)));
    }

    public void MapEndpoints(IEndpointRouteBuilder routes) => routes.MapTreasury();

    /// <remarks>
    /// Keycloak puts realm roles in a <c>realm_access</c> claim whose value is
    /// a JSON object, and the bearer handler does not unpack it. Read here, in
    /// the module that needs it, rather than in the platform — which is not
    /// allowed to know an identity provider exists. Catalog and P2P read their
    /// own roles the same way; three copies of ten lines is the price of that
    /// boundary, and it is cheaper than the alternative.
    /// </remarks>
    private static bool HasAdminRole(System.Security.Claims.ClaimsPrincipal user)
    {
        if (user.IsInRole(AdminRole)) return true;

        var realmAccess = user.FindFirst("realm_access")?.Value;
        if (string.IsNullOrWhiteSpace(realmAccess)) return false;

        try
        {
            using var document = JsonDocument.Parse(realmAccess);
            if (!document.RootElement.TryGetProperty("roles", out var roles)
                || roles.ValueKind != JsonValueKind.Array)
            {
                return false;
            }

            foreach (var role in roles.EnumerateArray())
            {
                if (role.ValueKind == JsonValueKind.String
                    && string.Equals(role.GetString(), AdminRole, StringComparison.Ordinal))
                {
                    return true;
                }
            }
        }
        catch (JsonException)
        {
            return false;
        }

        return false;
    }
}
