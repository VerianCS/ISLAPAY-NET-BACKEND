using IslaPay.Platform.AspNet;
using IslaPay.Platform.Data;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace IslaPay.Marketplace;

/// <summary>
/// The marketplace: listings, and payment held against them until a code is
/// scanned.
/// </summary>
/// <remarks>
/// <para>
/// Owns two tables and no money. Every movement goes through
/// <c>ILedger</c>, so the balance a buyer sees and the amount sitting in
/// escrow are the same set of double-entry records rather than two systems
/// that have to be reconciled.
/// </para>
/// <para>
/// This is the first module to use EF Core. The schema is still owned by the
/// <c>.sql</c> under <c>Migrations/</c> and applied by the platform's migrator
/// — EF maps, and does not migrate.
/// </para>
/// </remarks>
public sealed class MarketplaceModule : IIslaPayModule
{
    public string Name => "marketplace";

    public void AddServices(IHostApplicationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        var options = builder.Configuration.GetSection("Marketplace").Get<MarketplaceOptions>()
            ?? new MarketplaceOptions();

        builder.Services.AddSingleton(options);

        builder.Services.AddSingleton(new MigrationSet(
            Module: "marketplace",
            Assembly: typeof(MarketplaceModule).Assembly,
            ResourcePrefix: "IslaPay.Marketplace.Migrations."));

        builder.Services.AddDbContext<MarketplaceDbContext>((services, db) =>
        {
            // The connection string the platform already resolved. One database,
            // one pool configuration, one place it is configured.
            var data = services.GetRequiredService<DataOptions>();
            db.UseNpgsql(data.ConnectionString);
        });

        builder.Services.AddScoped<MarketplaceService>();
        // Declared so the treasury can check this module's books against the
        // ledger's. Scoped, like the DbContext it reads from.
        builder.Services.AddScoped<IEscrowReporter, MarketplaceEscrowReporter>();

        builder.Services.AddHostedService<HoldSweeper>();
    }

    public void MapEndpoints(IEndpointRouteBuilder routes) => routes.MapMarketplace();
}
