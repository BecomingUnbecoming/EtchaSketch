namespace EtchaSketch.Assistant;

/// <summary>Keeps the assistant's copy in step with the household: a sync every minute (by default).</summary>
public sealed class SyncLoop(AssistantData data, AssistantOptions options, ILogger<SyncLoop> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(Math.Max(10, options.SyncSeconds)));
        do
        {
            try
            {
                if (data.Household is not null)
                {
                    await data.UseAsync(d => d.Sync.SyncAsync(stoppingToken));
                }
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                // Tried again next time; a removed assistant keeps saying so until it's paired again.
                log.LogWarning("Sync failed: {Message}", exception.Message);
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
