using Spiza.Api.Configuration;

namespace Spiza.Api.Services;

public sealed class RefreshWorker(
    RefreshService refresh,
    SpizaOptions options,
    TimeProvider timeProvider,
    ILogger<RefreshWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var shouldRefresh = options.RefreshOnStart;
        IReadOnlySet<string>? retrySources = null;
        var retryAttempt = 0;

        while (!stoppingToken.IsCancellationRequested)
        {
            if (shouldRefresh)
            {
                try
                {
                    var results = await refresh.RefreshAsync(stoppingToken, retrySources);
                    if (results.Any(result => result.Status == "already_running"))
                    {
                        retrySources = new HashSet<string>(["all"], StringComparer.OrdinalIgnoreCase);
                        retryAttempt++;
                    }
                    else
                    {
                        var staleSources = results
                            .Where(result => result.Status is "failed" or "partial" or "stale")
                            .Select(result => result.Source)
                            .ToHashSet(StringComparer.OrdinalIgnoreCase);
                        retrySources = staleSources.Count == 0 ? null : staleSources;
                        retryAttempt = retrySources is null ? 0 : retryAttempt + 1;
                    }
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception exception)
                {
                    logger.LogError(exception, "Scheduled source refresh failed.");
                    retrySources = new HashSet<string>(["all"], StringComparer.OrdinalIgnoreCase);
                    retryAttempt++;
                }
            }

            shouldRefresh = false;
            var regularDelay = GetDelayUntilNextRegularRefresh(timeProvider.GetUtcNow(), options);
            var delay = regularDelay;
            if (retrySources is not null)
            {
                var retryDelay = GetRetryDelay(retryAttempt, options.RefreshRetryIntervalMinutes,
                    options.RefreshRetryMaxIntervalMinutes);
                if (retryDelay < regularDelay)
                {
                    delay = retryDelay;
                }
                else
                {
                    // The next scheduled full refresh comes first, so let it refresh every source.
                    retrySources = null;
                    retryAttempt = 0;
                }
            }

            try
            {
                await Task.Delay(delay, timeProvider, stoppingToken);
                shouldRefresh = true;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }
    }

    internal static TimeSpan GetDelayUntilNextRegularRefresh(DateTimeOffset nowUtc, SpizaOptions options)
    {
        if (options.RefreshIntervalMinutes != 1440)
        {
            return TimeSpan.FromMinutes(options.RefreshIntervalMinutes);
        }

        return GetDelayUntilNextDailyTime(nowUtc, options.TimeZone, options.RefreshDailyAt);
    }

    internal static TimeSpan GetDelayUntilNextDailyTime(DateTimeOffset nowUtc, TimeZoneInfo timeZone, TimeOnly dailyAt)
    {
        var localNow = TimeZoneInfo.ConvertTime(nowUtc, timeZone).DateTime;
        var nextLocal = DateTime.SpecifyKind(localNow.Date.Add(dailyAt.ToTimeSpan()), DateTimeKind.Unspecified);
        if (nextLocal <= localNow)
        {
            nextLocal = nextLocal.AddDays(1);
        }

        while (timeZone.IsInvalidTime(nextLocal))
        {
            nextLocal = nextLocal.AddMinutes(1);
        }

        var nextUtc = TimeZoneInfo.ConvertTimeToUtc(nextLocal, timeZone);
        return nextUtc - nowUtc.UtcDateTime;
    }

    internal static TimeSpan GetRetryDelay(int attempt, int baseMinutes, int maxMinutes)
    {
        var delayMinutes = baseMinutes;
        for (var index = 1; index < attempt && delayMinutes < maxMinutes; index++)
        {
            delayMinutes = Math.Min(delayMinutes * 2, maxMinutes);
        }

        return TimeSpan.FromMinutes(Math.Clamp(delayMinutes, 1, maxMinutes));
    }
}
