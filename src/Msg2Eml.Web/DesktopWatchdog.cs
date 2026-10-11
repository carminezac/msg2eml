namespace Msg2Eml.Web;

/// <summary>
/// Desktop mode: the page sends a heartbeat every couple of seconds while its window is open.
/// When heartbeats stop (window closed) the process exits; if no heartbeat ever arrives
/// (browser failed to open) it exits after a grace period.
/// </summary>
public sealed class DesktopWatchdog(Msg2EmlOptions options, IHostApplicationLifetime lifetime, ILogger<DesktopWatchdog> logger)
    : BackgroundService
{
    private long _lastTicks;
    private readonly long _startTicks = Environment.TickCount64;

    public void Touch() => Interlocked.Exchange(ref _lastTicks, Environment.TickCount64);

    /// <summary>Pure decision, unit-testable: should the process stop at <paramref name="nowTicks"/>?</summary>
    public static bool ShouldStop(long nowTicks, long startTicks, long lastHeartbeatTicks, int idleSeconds, int graceSeconds)
    {
        if (idleSeconds <= 0) return false;                                       // watchdog disabled
        if (lastHeartbeatTicks == 0) return nowTicks - startTicks > graceSeconds * 1000L;
        return nowTicks - lastHeartbeatTicks > idleSeconds * 1000L;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (options.DesktopIdleSeconds <= 0) return;
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                await Task.Delay(1000, stoppingToken);
                if (ShouldStop(Environment.TickCount64, _startTicks, Interlocked.Read(ref _lastTicks),
                        options.DesktopIdleSeconds, options.DesktopStartupGraceSeconds))
                {
                    logger.LogInformation(_lastTicks == 0 ? "desktop window never connected, exiting" : "desktop window closed, exiting");
                    lifetime.StopApplication();
                    return;
                }
            }
        }
        catch (OperationCanceledException) { }
    }
}
