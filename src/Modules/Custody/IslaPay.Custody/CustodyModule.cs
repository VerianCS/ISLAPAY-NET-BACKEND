using IslaPay.Platform.AspNet;
using IslaPay.Platform.Data;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace IslaPay.Custody;

/// <summary>
/// On-chain deposits: an address per user, and money that arrives at it.
/// </summary>
/// <remarks>
/// <para>
/// The first module whose main input is not a request. Everything else in this
/// system moves because somebody called an endpoint; a deposit moves because a
/// stranger broadcast a transaction, and the application finds out by reading
/// a block. That inverts the usual failure question from "did we do what was
/// asked" to "have we noticed what already happened, and exactly once".
/// </para>
/// <para>
/// Owns the <c>custody</c> schema and no money: the ledger credits, through
/// <c>ILedger</c>, and never before the chain is final.
/// </para>
/// <para>
/// It deliberately owns no keys either. Addresses come from
/// <see cref="IDepositAddresses"/>, which has no production implementation —
/// see the remarks there for why that is a decision rather than an omission.
/// </para>
/// </remarks>
public sealed class CustodyModule : IIslaPayModule
{
    public string Name => "custody";

    public void AddServices(IHostApplicationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        var options = builder.Configuration.GetSection("Custody").Get<CustodyOptions>()
            ?? new CustodyOptions();

        builder.Services.AddSingleton(options);

        builder.Services.AddSingleton(new MigrationSet(
            Module: "custody",
            Assembly: typeof(CustodyModule).Assembly,
            ResourcePrefix: "IslaPay.Custody.Migrations."));

        builder.Services.AddDbContext<CustodyDbContext>((services, db) =>
        {
            var data = services.GetRequiredService<DataOptions>();
            db.UseNpgsql(data.ConnectionString);
        });

        builder.Services.AddScoped<CustodyService>();
        builder.Services.AddHostedService<DepositSweeper>();

        // Development gets made-up addresses so the flow can be walked through
        // end to end. Everywhere else the host refuses to start — here and
        // now, not at the moment somebody asks for an address.
        //
        // The failure this prevents is specific and unrecoverable. A deferred
        // throw would mean the API comes up healthy and fails when a real
        // person wants somewhere to send money; worse, an implementation that
        // *returned* something would mean publishing an address nobody holds
        // the key to, and every deposit sent to it is lost permanently. There
        // is no support process for that.
        if (builder.Environment.IsDevelopment())
        {
            builder.Services.AddSingleton<IDepositAddresses, DevelopmentDepositAddresses>();
        }
        else
        {
            throw new InvalidOperationException(
                "No deposit-address source is configured. Register an IDepositAddresses "
                + $"implementation before running in {builder.Environment.EnvironmentName}; "
                + "the development one invents addresses that no key exists for.");
        }
    }

    public void MapEndpoints(IEndpointRouteBuilder routes) => routes.MapCustody();
}
