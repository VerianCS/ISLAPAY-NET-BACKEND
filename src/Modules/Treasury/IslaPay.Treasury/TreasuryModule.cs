using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using IslaPay.Platform.AspNet;
using IslaPay.Platform.Data;

namespace IslaPay.Treasury;

/// <summary>
/// The platform's own position, and the door money enters by.
/// </summary>
/// <remarks>
/// <para>
/// It keeps no figures of its own, and that is the design rather than an
/// omission. A treasury that kept its own balances would be a second set of
/// books, and the second set is always the one that is wrong. It reads the
/// ledger, asks each context with escrow what it is holding, and writes exactly
/// one kind of posting — after two people have agreed to it. Its one table
/// holds those agreements in progress, never a balance.
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
    public string Name => "treasury";

    public void AddServices(IHostApplicationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.Services.AddScoped<TreasuryService>();
        builder.Services.AddScoped<TreasuryProposals>();
        builder.Services.AddScoped<TreasuryIssuance>();

        // The E-ISLA that existed before the issuer, moved onto it once. After
        // the catalogue has loaded, which registered first and so runs first.
        builder.Services.AddSingleton<IStartupTask, IssuerGenesis>();

        // Its first table: proposals waiting for a second person. Requests,
        // not balances — the figures stay in the ledger.
        builder.Services.AddSingleton(new MigrationSet(
            Module: "treasury",
            Assembly: typeof(TreasuryModule).Assembly,
            ResourcePrefix: "IslaPay.Treasury.Migrations."));
    }

    public void MapEndpoints(IEndpointRouteBuilder routes) => routes.MapTreasury();
}

/// <summary>Runs <see cref="TreasuryIssuance.GenesisAsync"/> before the first request.</summary>
public sealed class IssuerGenesis(IServiceScopeFactory scopes) : IStartupTask
{
    public string Name => "treasury-issuer-genesis";

    public async Task RunAsync(CancellationToken cancellationToken = default)
    {
        await using var scope = scopes.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<TreasuryIssuance>()
            .GenesisAsync(cancellationToken).ConfigureAwait(false);
    }
}
