using System.Text.Json;
using IslaPay.Catalog.Contracts;
using IslaPay.Platform;
using IslaPay.Platform.AspNet;
using IslaPay.Platform.Data;
using IslaPay.Platform.Serialization;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace IslaPay.Catalog;

/// <summary>
/// The currencies and chains the system knows, as data.
/// </summary>
/// <remarks>
/// <para>
/// Underneath every other module, and the only one that has to be loaded
/// before anything else can answer a question. It replaces a four-member
/// <c>enum</c>: the set of currencies, their decimal places and their policy
/// are rows now, so a currency can be added or switched off without a release
/// — which is the difference between a system that can be multi-currency and
/// one that cannot.
/// </para>
/// <para>
/// It owns the <c>catalog</c> schema, no money and no behaviour beyond
/// answering questions and flipping switches.
/// </para>
/// </remarks>
public sealed class CatalogModule : IIslaPayModule
{
    /// <summary>The realm role that may switch a currency or chain on and off.</summary>
    public const string AdminRole = "catalog-admin";

    public string Name => "catalog";

    public void AddServices(IHostApplicationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.Services.AddSingleton(new MigrationSet(
            Module: "catalog",
            Assembly: typeof(CatalogModule).Assembly,
            ResourcePrefix: "IslaPay.Catalog.Migrations."));

        builder.Services.AddSingleton<PostgresCurrencyCatalog>();
        builder.Services.AddSingleton<ICurrencyCatalog>(
            s => s.GetRequiredService<PostgresCurrencyCatalog>());

        // The narrow slice anything that parses an amount needs. Registered
        // separately so a consumer can depend on "somewhere that knows decimal
        // places" without taking the whole catalogue.
        builder.Services.AddSingleton<ICurrencyScales>(
            s => s.GetRequiredService<PostgresCurrencyCatalog>());

        // Serializer options that can read money, because they know what the
        // codes mean. Anything resolving JsonSerializerOptions from the
        // container gets these; the static IslaPayJson.Options can only write.
        builder.Services.AddSingleton(s =>
            IslaPayJson.Create(s.GetRequiredService<ICurrencyScales>()));

        builder.Services.AddScoped<CatalogAdminService>();
        builder.Services.AddSingleton<IStartupTask, CatalogWarmUp>();

        builder.Services.AddAuthorizationBuilder()
            .AddPolicy(CatalogEndpoints.AdminPolicy, policy =>
                policy.RequireAssertion(context => HasAdminRole(context.User)));
    }

    public void MapEndpoints(IEndpointRouteBuilder routes) => routes.MapCatalog();

    /// <remarks>
    /// Keycloak puts realm roles in a <c>realm_access</c> claim whose value is
    /// a JSON object, and the bearer handler does not unpack it. The module
    /// that needs the role reads it, rather than teaching the platform about
    /// an identity provider it is not allowed to know exists.
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

/// <summary>
/// Loads the catalogue before the first request.
/// </summary>
/// <remarks>
/// It has to happen once and it has to happen early: a currency lookup is
/// synchronous, so there is no moment inside a request where the catalogue can
/// politely go and fetch itself. Failing here fails start-up, which is the
/// right place — a process serving traffic with no idea what a currency is
/// would answer every wallet read with an error.
/// </remarks>
public sealed class CatalogWarmUp : IStartupTask
{
    private readonly ICurrencyCatalog _catalog;

    public CatalogWarmUp(ICurrencyCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        _catalog = catalog;
    }

    public string Name => "currency catalogue";

    public Task RunAsync(CancellationToken cancellationToken = default) =>
        _catalog.RefreshAsync(cancellationToken);
}
