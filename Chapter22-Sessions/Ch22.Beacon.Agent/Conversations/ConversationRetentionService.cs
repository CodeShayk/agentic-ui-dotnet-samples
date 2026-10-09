namespace Ch22.Beacon.Agent.Conversations;

/// <summary>
/// Applies the retention settings at startup and every hour: whole threads go after
/// <see cref="ConversationStoreOptions.RetentionDays"/> without activity, raw event logs after
/// <see cref="ConversationStoreOptions.EventLogRetentionDays"/>. A conversation kept forever is a liability.
/// </summary>
public sealed partial class ConversationRetentionService(IConversationStore store, TimeProvider time, ILogger<ConversationRetentionService> logger)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromHours(1), time);
        do
        {
            try
            {
                var (threads, logs) = await store.ApplyRetentionAsync(stoppingToken);
                if (threads + logs > 0)
                {
                    LogApplied(logger, threads, logs);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                LogFailed(logger, ex);
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    [LoggerMessage(EventId = 2241, Level = LogLevel.Information, Message = "Retention removed {Threads} thread(s) and {Logs} event log(s).")]
    private static partial void LogApplied(ILogger logger, int threads, int logs);

    [LoggerMessage(EventId = 2242, Level = LogLevel.Warning, Message = "Retention couldn't finish; it will try again in an hour.")]
    private static partial void LogFailed(ILogger logger, Exception exception);
}
