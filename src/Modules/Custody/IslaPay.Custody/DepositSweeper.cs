using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace IslaPay.Custody;

/// <summary>
/// Finishes deposits that were interrupted between this module's commit and
/// the ledger's.
/// </summary>
/// <remarks>
/// It decides nothing. For every deposit stuck in <c>crediting</c> it asks the
/// ledger whether the posting exists and makes the row agree — which is the
/// only safe way to close that window, because the two plausible guesses
/// ("it worked" and "it did not") are each catastrophic in one direction.
/// </remarks>
public sealed partial class DepositSweeper : BackgroundService
{
    private readonly IServiceScopeFactory _scopes;
    private readonly CustodyOptions _options;
    private readonly ILogger<DepositSweeper> _log;

    public DepositSweeper(
        IServiceScopeFactory scopes, CustodyOptions options, ILogger<DepositSweeper> log)
    {
        ArgumentNullException.ThrowIfNull(scopes);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(log);
        _scopes = scopes;
        _options = options;
        _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(_options.SweepEvery);

        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
        {
            try
            {
                await using var scope = _scopes.CreateAsyncScope();
                var custody = scope.ServiceProvider.GetRequiredService<CustodyService>();
                var repaired = await custody
                    .RepairAsync(_options.InFlightGrace, stoppingToken)
                    .ConfigureAwait(false);

                if (repaired > 0) Repaired(_log, repaired);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception e)
            {
                // A sweep that throws must not end the sweeper: the next tick
                // is the retry, and a deposit left in flight is exactly what
                // this exists to resolve.
                SweepFailed(_log, e.Message);
            }
        }
    }

    [LoggerMessage(EventId = 1, Level = LogLevel.Information,
        Message = "Finished {Count} interrupted deposit credit(s).")]
    private static partial void Repaired(ILogger logger, int count);

    [LoggerMessage(EventId = 2, Level = LogLevel.Error,
        Message = "A deposit sweep failed and will be retried: {Error}")]
    private static partial void SweepFailed(ILogger logger, string error);
}
