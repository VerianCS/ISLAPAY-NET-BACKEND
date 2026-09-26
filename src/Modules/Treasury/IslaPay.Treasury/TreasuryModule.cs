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
    public string Name => "treasury";

    public void AddServices(IHostApplicationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.Services.AddScoped<TreasuryService>();
    }

    public void MapEndpoints(IEndpointRouteBuilder routes) => routes.MapTreasury();
}
