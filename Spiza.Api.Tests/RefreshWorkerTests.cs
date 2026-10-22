using Microsoft.Extensions.Configuration;
using Spiza.Api.Configuration;
using Spiza.Api.Services;

namespace Spiza.Api.Tests;

public sealed class RefreshWorkerTests
{
    [Theory]
    [InlineData("2026-01-01T22:30:00+00:00", 35)]
    [InlineData("2026-07-01T21:30:00+00:00", 35)]
    public void GetDelayUntilNextDailyTime_AnchorsToZagrebMidnightSchedule(string timestamp, int expectedMinutes)
    {
        var now = DateTimeOffset.Parse(timestamp);
        var zone = TimeZoneInfo.FindSystemTimeZoneById("Europe/Zagreb");

        var delay = RefreshWorker.GetDelayUntilNextDailyTime(now, zone, new TimeOnly(0, 5));

        Assert.Equal(TimeSpan.FromMinutes(expectedMinutes), delay);
    }

    [Fact]
    public void GetDelayUntilNextRegularRefresh_UsesDailyScheduleForDefaultInterval()
    {
        var options = SpizaOptions.FromConfiguration(new ConfigurationBuilder().Build());
        var now = new DateTimeOffset(2026, 1, 1, 22, 30, 0, TimeSpan.Zero);

        var delay = RefreshWorker.GetDelayUntilNextRegularRefresh(now, options);

        Assert.Equal(TimeSpan.FromMinutes(35), delay);
    }

    [Fact]
    public void GetDelayUntilNextRegularRefresh_PreservesCustomElapsedInterval()
    {
        var options = SpizaOptions.FromConfiguration(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["SPIZA_REFRESH_INTERVAL_MINUTES"] = "60"
            }).Build());

        var delay = RefreshWorker.GetDelayUntilNextRegularRefresh(DateTimeOffset.UtcNow, options);

        Assert.Equal(TimeSpan.FromHours(1), delay);
    }

    [Theory]
    [InlineData(1, 15)]
    [InlineData(2, 30)]
    [InlineData(3, 60)]
    [InlineData(4, 120)]
    [InlineData(5, 240)]
    [InlineData(6, 240)]
    public void GetRetryDelay_DoublesAndCapsDelay(int attempt, int expectedMinutes)
    {
        var delay = RefreshWorker.GetRetryDelay(attempt, 15, 240);

        Assert.Equal(TimeSpan.FromMinutes(expectedMinutes), delay);
    }
}
