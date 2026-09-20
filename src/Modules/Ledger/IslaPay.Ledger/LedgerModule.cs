using IslaPay.Ledger.Contracts;
using IslaPay.Platform.AspNet;
using IslaPay.Platform.Data;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace IslaPay.Ledger;

/// <summary>
/// The ledger: no routes of its own, and deliberately so.
/// </summary>
/// <remarks>
/// A double-entry ledger is not something a client talks to. It has no view a
/// user would recognise — no "transactions" in the sense the app means, no
/// balance that is anyone's in particular without a projection. Wallet is what
/// turns entries into a screen; this module only guarantees they add up.
/// <para>
/// So it contributes a schema, an implementation of <see cref="ILedger"/>, and
/// nothing to the HTTP surface.
/// </para>
/// </remarks>
public sealed class LedgerModule : IIslaPayModule
{
    public string Name => "ledger";

    public void AddServices(IHostApplicationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.Services.AddSingleton(new MigrationSet(
            Module: "ledger",
            Assembly: typeof(LedgerModule).Assembly,
            ResourcePrefix: "IslaPay.Ledger.Migrations."));

        builder.Services.AddSingleton<PostgresLedger>();
        builder.Services.AddSingleton<ILedger>(sp => sp.GetRequiredService<PostgresLedger>());
    }

    public void MapEndpoints(IEndpointRouteBuilder routes)
    {
        // Nothing. See the remarks above.
    }
}
