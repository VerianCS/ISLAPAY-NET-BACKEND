using IslaPay.Catalog.Contracts;
using IslaPay.Platform.Api;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace IslaPay.Catalog;

/// <summary>
/// What money there is, and the switches that decide it.
/// </summary>
/// <remarks>
/// The read side is open to any signed-in caller, because a client that
/// hard-codes the currency list is a client that offers a currency nothing can
/// settle. The write side is how a currency, a chain or an asset-on-a-chain is
/// switched on without a release — which is the whole point of the tables.
/// </remarks>
public static class CatalogEndpoints
{
    public const string AdminPolicy = "catalog-admin";

    public static IEndpointRouteBuilder MapCatalog(this IEndpointRouteBuilder routes)
    {
        ArgumentNullException.ThrowIfNull(routes);

        var read = routes.MapGroup("/v1/catalog").WithTags("Catalog").RequireAuthorization();

        read.MapGet("/currencies", (ICurrencyCatalog catalog, bool? all) =>
            Results.Ok(catalog.Currencies
                .Where(c => all == true || c.Enabled)
                .Select(c => new
                {
                    code = c.Code,
                    name = c.Name,
                    scale = c.Scale,
                    kind = c.Kind,
                    symbol = c.Symbol,
                    customerHoldable = c.CustomerHoldable,
                    enabled = c.Enabled,
                })));

        read.MapGet("/networks", (ICurrencyCatalog catalog, string? currency) =>
            Results.Ok(currency is { Length: > 0 }
                ? catalog.NetworksFor(currency).Where(n => n.Enabled).Select(Project)
                : catalog.Networks.Where(n => n.Enabled).Select(n => new
                {
                    id = n.Id,
                    name = n.Name,
                    confirmations = n.Confirmations,
                    memoRequired = n.MemoRequired,
                })));

        var admin = routes.MapGroup("/v1/admin/catalog")
            .WithTags("Catalog")
            .RequireAuthorization(AdminPolicy);

        // Everything, switched on or not: deciding what to switch on requires
        // seeing what is switched off.
        admin.MapGet("/currencies", (ICurrencyCatalog catalog) => Results.Ok(catalog.Currencies));

        admin.MapPut("/currencies/{code}/enabled", async (
            string code, bool value, CatalogAdminService admin_, CancellationToken ct) =>
        {
            await admin_.SetCurrencyEnabledAsync(code, value, ct).ConfigureAwait(false);
            return Results.NoContent();
        });

        admin.MapPut("/networks/{id}/enabled", async (
            string id, bool value, CatalogAdminService admin_, CancellationToken ct) =>
        {
            await admin_.SetNetworkEnabledAsync(id, value, ct).ConfigureAwait(false);
            return Results.NoContent();
        });

        admin.MapPut("/currencies/{code}/networks/{networkId}/enabled", async (
            string code, string networkId, bool value,
            CatalogAdminService admin_, CancellationToken ct) =>
        {
            await admin_.SetPairEnabledAsync(code, networkId, value, ct).ConfigureAwait(false);
            return Results.NoContent();
        });

        return routes;
    }

    private static object Project(CurrencyOnNetwork n) => new
    {
        id = n.NetworkId,
        name = n.NetworkName,
        tokenStandard = n.TokenStandard,
        confirmations = n.Confirmations,
        memoRequired = n.MemoRequired,
        minimumWithdrawal = n.MinimumWithdrawalMinor,
    };
}

/// <summary>A refusal the platform can translate without knowing this module.</summary>
public sealed class CatalogException : Exception, IApiFailure
{
    public CatalogException(string code, int status, string message) : base(message)
    {
        Code = code;
        Status = status;
    }

    public string Code { get; }

    public int Status { get; }

    public IReadOnlyDictionary<string, object>? Meta => null;
}
