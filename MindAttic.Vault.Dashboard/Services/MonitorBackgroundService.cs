using Microsoft.Extensions.Options;

namespace MindAttic.Vault.Dashboard.Services;

/// <summary>
/// Drives the health sweep on a fixed cadence (default hourly). Runs one sweep
/// immediately at startup so the dashboard is populated the moment it loads, then
/// ticks on <see cref="MonitorOptions.Interval"/>. A sweep that throws is logged
/// and swallowed so a single bad run never kills the schedule.
/// </summary>
public sealed class MonitorBackgroundService : BackgroundService
{
    private readonly LlmHealthMonitor monitor;
    private readonly MonitorOptions options;
    private readonly ILogger<MonitorBackgroundService> log;

    public MonitorBackgroundService(
        LlmHealthMonitor monitor,
        IOptions<MonitorOptions> options,
        ILogger<MonitorBackgroundService> log)
    {
        this.monitor = monitor;
        this.options = options.Value;
        this.log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = options.Interval <= TimeSpan.Zero ? TimeSpan.FromHours(1) : options.Interval;
        log.LogInformation("LLM monitor started — sweeping every {Interval}", interval);

        // Immediate first sweep, then on the interval.
        await SafeSweepAsync(stoppingToken);

        using var timer = new PeriodicTimer(interval);
        while (await timer.WaitForNextTickAsync(stoppingToken))
            await SafeSweepAsync(stoppingToken);
    }

    private async Task SafeSweepAsync(CancellationToken ct)
    {
        try
        {
            await monitor.RunSweepAsync(ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Shutting down — let it propagate out of the loop.
            throw;
        }
        catch (Exception ex)
        {
            log.LogError(ex, "LLM monitor sweep failed — will retry next tick");
        }
    }
}
