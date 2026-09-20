using System.Security.Claims;
using IslaPay.Platform.Api;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace IslaPay.Wallet;

/// <summary>
/// <c>/v1/me/wallet</c> and <c>/v1/me/transactions</c>, per <c>API_CONTRACT.md</c> §3.
/// </summary>
public static class WalletEndpoints
{
    public static RouteGroupBuilder MapWallet(this IEndpointRouteBuilder routes)
    {
        ArgumentNullException.ThrowIfNull(routes);

        var group = routes.MapGroup("/v1/me").WithTags("Wallet").RequireAuthorization();

        // D1: one request, because the screen needs balances, rates and
        // history together, and three round trips on a poor mobile connection
        // is three chances to fail.
        group.MapGet("/wallet", async (
            ClaimsPrincipal caller, WalletService wallet, CancellationToken ct) =>
            Results.Ok(await wallet.ReadAsync(SubjectOf(caller), 20, ct).ConfigureAwait(false)));

        group.MapGet("/transactions", async (
            ClaimsPrincipal caller, WalletService wallet,
            int? limit, string? cursor, CancellationToken ct) =>
            Results.Ok(await wallet
                .HistoryAsync(SubjectOf(caller), Math.Clamp(limit ?? 20, 1, 100), cursor, ct)
                .ConfigureAwait(false)));

        return group;
    }

    /// <summary>
    /// Whose wallet, taken from the validated token and never from the request.
    /// </summary>
    /// <remarks>
    /// There is no user id in any path or query above, deliberately. An
    /// endpoint that accepted one would need an authorisation check to stop it
    /// serving somebody else's balances, and the check that cannot be
    /// forgotten is the one there is no parameter for.
    /// </remarks>
    private static string SubjectOf(ClaimsPrincipal caller) =>
        caller.FindFirstValue(ClaimTypes.NameIdentifier)
        ?? caller.FindFirstValue("sub")
        ?? throw new WalletException(
            PlatformErrors.TokenInvalid, StatusCodes.Status401Unauthorized,
            "The token carries no subject.");
}

/// <summary>A Wallet failure the platform can translate without knowing Wallet.</summary>
public sealed class WalletException : Exception, IApiFailure
{
    public WalletException(string code, int status, string message) : base(message)
    {
        Code = code;
        Status = status;
    }

    public string Code { get; }

    public int Status { get; }

    public IReadOnlyDictionary<string, object>? Meta => null;
}
