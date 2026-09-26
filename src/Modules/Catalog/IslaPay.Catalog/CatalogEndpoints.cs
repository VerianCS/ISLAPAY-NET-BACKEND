using IslaPay.Catalog.Contracts;
using IslaPay.Platform.Api;
using IslaPay.Platform.AspNet.Security;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
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
    public static IEndpointRouteBuilder MapCatalog(this IEndpointRouteBuilder routes)
    {
        ArgumentNullException.ThrowIfNull(routes);

        var read = routes.MapGroup("/v1/catalog").WithTags("Catalog").RequireAuthorization();

        read.MapGet("/currencies", (ICurrencyCatalog catalog, bool? all) =>
            TypedResults.Ok<IReadOnlyList<CurrencyDto>>(
            [
                .. catalog.Currencies
                    .Where(c => all == true || c.Enabled)
                    .Select(c => new CurrencyDto(
                        c.Code, c.Name, c.Scale, c.Kind, c.Symbol, c.CustomerHoldable, c.Enabled)),
            ]));

        // Two shapes, and the declared return type says so rather than
        // hiding it behind `object`. Asking about one currency's chains is a
        // different question from asking which chains exist — the first
        // answers with the token's standard and its minimum, which the second
        // has no way to know — and a client generated from this is told both
        // instead of being handed whichever one the person writing it
        // happened to try first.
        read.MapGet("/networks",
            Results<Ok<IReadOnlyList<CurrencyNetworkDto>>, Ok<IReadOnlyList<NetworkDto>>> (
                ICurrencyCatalog catalog, string? currency) =>
            currency is { Length: > 0 }
                ? TypedResults.Ok<IReadOnlyList<CurrencyNetworkDto>>(
                    [.. catalog.NetworksFor(currency).Where(n => n.Enabled).Select(Project)])
                : TypedResults.Ok<IReadOnlyList<NetworkDto>>(
                [
                    .. catalog.Networks
                        .Where(n => n.Enabled)
                        .Select(n => new NetworkDto(
                            n.Id, n.Name, n.Confirmations, n.MemoRequired)),
                ]));

        var admin = routes.MapGroup("/v1/admin/catalog")
            .WithTags("Catalog")
            .RequirePermission(Permissions.CatalogManage);

        // Everything, switched on or not: deciding what to switch on requires
        // seeing what is switched off.
        admin.MapGet("/currencies", (ICurrencyCatalog catalog) => TypedResults.Ok(catalog.Currencies));

        admin.MapPut("/currencies/{code}/enabled", async (
            string code, bool value, CatalogAdminService admin_, CancellationToken ct) =>
        {
            await admin_.SetCurrencyEnabledAsync(code, value, ct).ConfigureAwait(false);
            return TypedResults.NoContent();
        });

        admin.MapPut("/networks/{id}/enabled", async (
            string id, bool value, CatalogAdminService admin_, CancellationToken ct) =>
        {
            await admin_.SetNetworkEnabledAsync(id, value, ct).ConfigureAwait(false);
            return TypedResults.NoContent();
        });

        admin.MapPut("/currencies/{code}/networks/{networkId}/enabled", async (
            string code, string networkId, bool value,
            CatalogAdminService admin_, CancellationToken ct) =>
        {
            await admin_.SetPairEnabledAsync(code, networkId, value, ct).ConfigureAwait(false);
            return TypedResults.NoContent();
        });

        return routes;
    }

    private static CurrencyNetworkDto Project(CurrencyOnNetwork n) => new(
        n.NetworkId,
        n.NetworkName,
        n.TokenStandard,
        n.Confirmations,
        n.MemoRequired,
        n.MinimumWithdrawalMinor);
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
