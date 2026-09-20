using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace IslaPay.Marketplace;

/// <summary>
/// Returns holds nobody scanned, and finishes what a crash interrupted.
/// </summary>
/// <remarks>
/// <para>
/// Without this the marketplace is only correct while nothing goes wrong. A
/// hold that expires with no sweeper never comes back; an order whose ledger
/// posting committed a millisecond before the process died stays in flight for
/// ever. Both leave a customer's money somewhere they cannot see it.
/// </para>
/// <para>
/// Every instance runs one. That is safe rather than merely tolerable: the
/// repair claims each row with <c>SELECT … FOR UPDATE</c> and every ledger
/// movement is keyed, so two sweepers racing produce one outcome and one
/// posting. It is wasted work, not a second payout.
/// </para>
/// </remarks>
public sealed partial class HoldSweeper : BackgroundService
{
    private readonly IServiceProvider _services;
    private readonly MarketplaceOptions _options;
    private readonly ILogger<HoldSweeper> _log;

    public HoldSweeper(
        IServiceProvider services, MarketplaceOptions options, ILogger<HoldSweeper> log)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(log);
        _services = services;
        _options = options;
        _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(_options.SweepInterval);

        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
        {
            try
            {
                // A scope per pass: the DbContext is scoped, and one that lived
                // for the life of the process would accumulate every order it
                // had ever tracked.
                await using var scope = _services.CreateAsyncScope();
                var marketplace = scope.ServiceProvider.GetRequiredService<MarketplaceService>();

                var touched = await marketplace.RepairAsync(stoppingToken).ConfigureAwait(false);
                if (touched > 0) Swept(_log, touched);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                // Logged and retried on the next tick. A sweeper that dies on
                // one bad row stops returning everybody else's money.
                SweepFailed(_log, e.Message);
            }
        }
    }

    [LoggerMessage(EventId = 1, Level = LogLevel.Information,
        Message = "Settled or repaired {count} marketplace orders.")]
    private static partial void Swept(ILogger logger, int count);

    [LoggerMessage(EventId = 2, Level = LogLevel.Error,
        Message = "A marketplace sweep failed: {error}. Retrying on the next tick.")]
    private static partial void SweepFailed(ILogger logger, string error);
}
