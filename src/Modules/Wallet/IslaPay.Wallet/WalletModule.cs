using IslaPay.Identity.Contracts;
using IslaPay.Platform.AspNet;
using IslaPay.Platform.Messaging;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace IslaPay.Wallet;

/// <summary>
/// The wallet the customer sees.
/// </summary>
/// <remarks>
/// Owns no tables. Its state is the ledger's, read through <c>ILedger</c>, and
/// giving it a second copy would create two answers to "what is my balance" —
/// which is the kind of thing that is discovered by a customer.
/// </remarks>
public sealed class WalletModule : IIslaPayModule
{
    public string Name => "wallet";

    public void AddServices(IHostApplicationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.Services.AddScoped<WalletService>();
        builder.Services.AddScoped<UserRegisteredHandler>();

        // Bound to every user event rather than just the one handled today, so
        // that adding a second one is a change to the handler and not to the
        // broker's topology.
        builder.Services.AddSingleton(new Subscription(
            Consumer: "wallet",
            Context: IdentityEvents.Context,
            BindingPattern: IdentityEvents.AllUserEvents,
            HandlerType: typeof(UserRegisteredHandler)));
    }

    public void MapEndpoints(IEndpointRouteBuilder routes) => routes.MapWallet();
}
