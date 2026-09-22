using System.Text.Json;
using IslaPay.Platform.AspNet;
using IslaPay.Platform.Data;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace IslaPay.P2P;

/// <summary>
/// Instant exchange between the wallet and local money.
/// </summary>
/// <remarks>
/// <para>
/// The rail money leaves and arrives by. Every other module moves value
/// between accounts that already exist inside IslaPay; this one is the only
/// place where the boundary is crossed, which is why it is the module that
/// needs a person in the loop and a queue for them to work.
/// </para>
/// <para>
/// Owns the <c>p2p</c> schema and no money. Every movement goes through
/// <c>ILedger</c>, including the local-currency obligation — that is what
/// <c>Currency.Cup</c> exists for.
/// </para>
/// </remarks>
public sealed class P2PModule : IIslaPayModule
{
    /// <summary>
    /// The realm role that may settle trades.
    /// </summary>
    /// <remarks>
    /// A realm role rather than a client role, so it survives the API being
    /// split into more than one client, and so an operator console added later
    /// inherits it without a second grant.
    /// </remarks>
    public const string OperatorRole = "p2p-operator";

    public string Name => "p2p";

    public void AddServices(IHostApplicationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        var options = builder.Configuration.GetSection("P2P").Get<P2POptions>() ?? new P2POptions();

        builder.Services.AddSingleton(options);

        builder.Services.AddSingleton(new MigrationSet(
            Module: "p2p",
            Assembly: typeof(P2PModule).Assembly,
            ResourcePrefix: "IslaPay.P2P.Migrations."));

        builder.Services.AddDbContext<P2PDbContext>((services, db) =>
        {
            var data = services.GetRequiredService<DataOptions>();
            db.UseNpgsql(data.ConnectionString);
        });

        builder.Services.AddScoped<P2PService>();
        // Declared so the treasury can check this module's books against the
        // ledger's. Scoped, like the DbContext it reads from.
        builder.Services.AddScoped<IEscrowReporter, P2PEscrowReporter>();

        builder.Services.AddHostedService<TradeSweeper>();

        // Additive: the platform has already called AddAuthorization, and this
        // contributes one policy to it rather than replacing anything.
        builder.Services.AddAuthorizationBuilder()
            .AddPolicy(P2PEndpoints.OperatorPolicy, policy =>
                policy.RequireAssertion(context => HasOperatorRole(context.User)));
    }

    public void MapEndpoints(IEndpointRouteBuilder routes) => routes.MapP2P();

    /// <summary>
    /// Whether the token carries the operator role.
    /// </summary>
    /// <remarks>
    /// Keycloak puts realm roles in a <c>realm_access</c> claim whose value is
    /// a JSON object, and the bearer handler does not unpack it into
    /// <c>ClaimTypes.Role</c>. Rather than teach the platform about Keycloak's
    /// token shape — which would put an identity provider's detail in a
    /// project that is not allowed to know one exists — the module that needs
    /// the role reads it.
    /// </remarks>
    private static bool HasOperatorRole(System.Security.Claims.ClaimsPrincipal user)
    {
        // Honoured if something upstream did map it, so this keeps working if
        // the platform ever grows a role mapper.
        if (user.IsInRole(OperatorRole)) return true;

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
                    && string.Equals(role.GetString(), OperatorRole, StringComparison.Ordinal))
                {
                    return true;
                }
            }
        }
        catch (JsonException)
        {
            // A token whose claim will not parse grants nothing. Refusing is
            // the only safe reading of something unreadable.
            return false;
        }

        return false;
    }
}
