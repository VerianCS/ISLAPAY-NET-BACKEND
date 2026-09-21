using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace IslaPay.P2P;

/// <summary>
/// Finishes what a crash interrupted, and stops expecting buys nobody paid.
/// </summary>
/// <remarks>
/// <para>
/// What it deliberately does not do is give a seller their money back. A
/// committed sell has taken somebody's balance and the only honest endings are
/// paying them or refunding them — neither of which a timer is entitled to
/// decide. It waits for a person however long that takes, and the queue is
/// where that wait is visible.
/// </para>
/// <para>
/// Every instance runs one. That is safe rather than merely tolerable: each
/// repair claims its row with <c>SELECT … FOR UPDATE</c> and every movement is
/// keyed, so two sweepers racing produce one outcome and one posting.
/// </para>
/// </remarks>
public sealed partial class TradeSweeper : BackgroundService
{
    private readonly IServiceProvider _services;
    private readonly P2POptions _options;
    private readonly ILogger<TradeSweeper> _log;

    public TradeSweeper(
        IServiceProvider services, P2POptions options, ILogger<TradeSweeper> log)
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
                // for the life of the process would accumulate every trade it
                // had ever tracked.
                await using var scope = _services.CreateAsyncScope();
                var p2p = scope.ServiceProvider.GetRequiredService<P2PService>();

                var touched = await p2p.RepairAsync(stoppingToken).ConfigureAwait(false);
                if (touched > 0) Swept(_log, touched);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                SweepFailed(_log, e.Message);
            }
        }
    }

    [LoggerMessage(EventId = 1, Level = LogLevel.Information,
        Message = "Repaired or expired {count} P2P trades.")]
    private static partial void Swept(ILogger logger, int count);

    [LoggerMessage(EventId = 2, Level = LogLevel.Error,
        Message = "A P2P sweep failed: {error}. Retrying on the next tick.")]
    private static partial void SweepFailed(ILogger logger, string error);
}
