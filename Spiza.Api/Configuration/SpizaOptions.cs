using System.Globalization;
using Microsoft.Extensions.Logging;

namespace Spiza.Api.Configuration;

public sealed class SpizaOptions
{
    private const string DefaultTimeZoneId = "Europe/Zagreb";
    public int Port { get; private init; } = 7492;
    public string DatabasePath { get; private init; } = "data/spiza.db";
    public string? ApiKey { get; private init; }
    public bool RefreshOnStart { get; private init; } = true;
    public int RefreshIntervalMinutes { get; private init; } = 1440;
    public TimeOnly RefreshDailyAt { get; private init; } = new(0, 5);
    public int RefreshRetryIntervalMinutes { get; private init; } = 15;
    public int RefreshRetryMaxIntervalMinutes { get; private init; } = 240;
    public string TimeZoneId { get; private init; } = DefaultTimeZoneId;
    public TimeZoneInfo TimeZone { get; private init; } = TimeZoneInfo.Utc;
    public DateTime Today => GetLocalDate(DateTimeOffset.UtcNow, TimeZone);
    public int SourceTimeoutSeconds { get; private init; } = 30;
    public int SourceRetryCount { get; private init; } = 3;
    public int SourceRetryDelaySeconds { get; private init; } = 2;
    public int SourceRequestDelayMilliseconds { get; private init; } = 1100;
    public int SourceRequestBudget { get; private init; } = 800;
    public int HistoryRetentionDays { get; private init; } = 30;
    public int BatchMax { get; private init; } = 100;
    public int SearchMaxResults { get; private init; } = 50;
    public int ExportMaxRows { get; private init; }
    public int SqliteBusyTimeoutMilliseconds { get; private init; } = 5000;
    public bool KonzumEnabled { get; private init; } = true;
    public bool LidlEnabled { get; private init; } = true;
    public bool TommyEnabled { get; private init; } = true;
    public bool SparEnabled { get; private init; } = true;
    public bool EurospinEnabled { get; private init; } = true;
    public bool PlodineEnabled { get; private init; } = true;
    public bool KauflandEnabled { get; private init; } = true;
    public bool DmEnabled { get; private init; } = true;
    public bool RibolaEnabled { get; private init; } = true;
    public bool MetroEnabled { get; private init; } = true;
    public string EffectivePriceRule { get; private init; } = "promo-if-active";
    public LogLevel MinimumLogLevel { get; private init; } = LogLevel.Information;

    public static SpizaOptions FromConfiguration(IConfiguration configuration)
    {
        var port = ReadInt(configuration, "SPIZA_PORT", 7492, 1, 65535);
        var priceRule = configuration["SPIZA_EFFECTIVE_PRICE_RULE"]?.Trim().ToLowerInvariant();
        if (priceRule is not (null or "promo-if-active" or "regular"))
        {
            priceRule = "promo-if-active";
        }

        var logLevel = Enum.TryParse<LogLevel>(configuration["SPIZA_LOG_LEVEL"], true, out var parsedLogLevel)
            ? parsedLogLevel
            : LogLevel.Information;
        var timeZoneId = string.IsNullOrWhiteSpace(configuration["SPIZA_TIME_ZONE_ID"])
            ? DefaultTimeZoneId
            : configuration["SPIZA_TIME_ZONE_ID"]!.Trim();
        if (!TimeZoneInfo.TryFindSystemTimeZoneById(timeZoneId, out var timeZone))
        {
            timeZoneId = DefaultTimeZoneId;
            timeZone = TimeZoneInfo.FindSystemTimeZoneById(DefaultTimeZoneId);
        }

        var refreshDailyAt = TimeOnly.TryParseExact(configuration["SPIZA_REFRESH_DAILY_AT"], "HH:mm",
            CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsedRefreshDailyAt)
            ? parsedRefreshDailyAt
            : new TimeOnly(0, 5);
        var refreshRetryIntervalMinutes = ReadInt(configuration, "SPIZA_REFRESH_RETRY_INTERVAL_MINUTES", 15, 1, 1440);
        var refreshRetryMaxIntervalMinutes = ReadInt(configuration, "SPIZA_REFRESH_RETRY_MAX_INTERVAL_MINUTES", 240, 1, 1440);

        return new SpizaOptions
        {
            Port = port,
            DatabasePath = string.IsNullOrWhiteSpace(configuration["SPIZA_DATABASE_PATH"])
                ? "data/spiza.db"
                : configuration["SPIZA_DATABASE_PATH"]!.Trim(),
            ApiKey = NullIfWhiteSpace(configuration["SPIZA_API_KEY"]),
            RefreshOnStart = ReadBool(configuration, "SPIZA_REFRESH_ON_START", true),
            RefreshIntervalMinutes = ReadInt(configuration, "SPIZA_REFRESH_INTERVAL_MINUTES", 1440, 1, 10080),
            RefreshDailyAt = refreshDailyAt,
            RefreshRetryIntervalMinutes = refreshRetryIntervalMinutes,
            RefreshRetryMaxIntervalMinutes = Math.Max(refreshRetryIntervalMinutes, refreshRetryMaxIntervalMinutes),
            TimeZoneId = timeZoneId,
            TimeZone = timeZone,
            SourceTimeoutSeconds = ReadInt(configuration, "SPIZA_SOURCE_TIMEOUT_SECONDS", 30, 1, 600),
            SourceRetryCount = ReadInt(configuration, "SPIZA_SOURCE_RETRY_COUNT", 3, 0, 10),
            SourceRetryDelaySeconds = ReadInt(configuration, "SPIZA_SOURCE_RETRY_DELAY_SECONDS", 2, 0, 300),
            SourceRequestDelayMilliseconds = ReadInt(configuration, "SPIZA_SOURCE_REQUEST_DELAY_MS", 1100, 0, 60000),
            SourceRequestBudget = ReadInt(configuration, "SPIZA_SOURCE_REQUEST_BUDGET", 800, 1, 10000),
            HistoryRetentionDays = ReadInt(configuration, "SPIZA_HISTORY_RETENTION_DAYS", 30, 0, 36500),
            BatchMax = ReadInt(configuration, "SPIZA_BATCH_MAX", 100, 1, 5000),
            SearchMaxResults = ReadInt(configuration, "SPIZA_SEARCH_MAX_RESULTS", 50, 1, 1000),
            ExportMaxRows = ReadInt(configuration, "SPIZA_EXPORT_MAX_ROWS", 0, 0, 10000000),
            SqliteBusyTimeoutMilliseconds = ReadInt(configuration, "SPIZA_SQLITE_BUSY_TIMEOUT_MS", 5000, 100, 60000),
            KonzumEnabled = ReadBool(configuration, "SPIZA_KONZUM_ENABLED", true),
            LidlEnabled = ReadBool(configuration, "SPIZA_LIDL_ENABLED", true),
            TommyEnabled = ReadBool(configuration, "SPIZA_TOMMY_ENABLED", true),
            SparEnabled = ReadBool(configuration, "SPIZA_SPAR_ENABLED", true),
            EurospinEnabled = ReadBool(configuration, "SPIZA_EUROSPIN_ENABLED", true),
            PlodineEnabled = ReadBool(configuration, "SPIZA_PLODINE_ENABLED", true),
            KauflandEnabled = ReadBool(configuration, "SPIZA_KAUFLAND_ENABLED", true),
            DmEnabled = ReadBool(configuration, "SPIZA_DM_ENABLED", true),
            RibolaEnabled = ReadBool(configuration, "SPIZA_RIBOLA_ENABLED", true),
            MetroEnabled = ReadBool(configuration, "SPIZA_METRO_ENABLED", true),
            EffectivePriceRule = priceRule ?? "promo-if-active",
            MinimumLogLevel = logLevel
        };
    }

    private static int ReadInt(IConfiguration configuration, string key, int fallback, int min, int max)
    {
        return int.TryParse(configuration[key], NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
            ? Math.Clamp(value, min, max)
            : fallback;
    }

    private static bool ReadBool(IConfiguration configuration, string key, bool fallback) =>
        bool.TryParse(configuration[key], out var value) ? value : fallback;

    public static DateTime GetLocalDate(DateTimeOffset utcNow, TimeZoneInfo timeZone) =>
        TimeZoneInfo.ConvertTime(utcNow, timeZone).Date;

    private static string? NullIfWhiteSpace(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

}
