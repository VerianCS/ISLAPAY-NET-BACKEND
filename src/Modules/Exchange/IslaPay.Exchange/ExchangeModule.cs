using IslaPay.Exchange.Contracts;
using IslaPay.Platform.AspNet;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace IslaPay.Exchange;

/// <summary>
/// Conversions between the currencies a customer holds.
/// </summary>
/// <remarks>
/// No schema. Quotes are signed with the host's data-protection keys, which
/// is the one thing to get right when there is more than one instance: every
/// instance must share the key ring, or a quote priced by one is refused by
/// the next.
/// </remarks>
public sealed class ExchangeModule : IIslaPayModule
{
    public string Name => "exchange";

    public void AddServices(IHostApplicationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        var options = builder.Configuration.GetSection("Exchange").Get<ExchangeOptions>() ?? new ExchangeOptions();
        builder.Services.AddSingleton(options);
        builder.Services.AddDataProtection();
        builder.Services.AddScoped<ExchangeService>();
        builder.Services.AddScoped<IExchangeRates>(s => s.GetRequiredService<ExchangeService>());
    }

    public void MapEndpoints(IEndpointRouteBuilder routes) => routes.MapExchange();
}
