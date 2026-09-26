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
    }

    public void MapEndpoints(IEndpointRouteBuilder routes) => routes.MapP2P();
}
