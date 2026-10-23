using Microsoft.Extensions.Configuration;
using Spiza.Api.Configuration;

namespace Spiza.Api.Tests;

public sealed class SpizaOptionsTests
{
    [Fact]
    public void FromConfiguration_UsesContainerFriendlyDefaultsWhenEnvironmentIsEmpty()
    {
        var options = SpizaOptions.FromConfiguration(new ConfigurationBuilder().Build());

        Assert.Equal(7492, options.Port);
        Assert.Equal("data/spiza.db", options.DatabasePath);
        Assert.True(options.RefreshOnStart);
        Assert.Equal(new TimeOnly(0, 5), options.RefreshDailyAt);
        Assert.Equal(15, options.RefreshRetryIntervalMinutes);
        Assert.Equal(240, options.RefreshRetryMaxIntervalMinutes);
        Assert.Equal("Europe/Zagreb", options.TimeZoneId);
        Assert.Equal("promo-if-active", options.EffectivePriceRule);
        Assert.Equal(1_000_000_000, options.SourceArchiveMaxExpandedBytes);
        Assert.Equal(["Split", "Solin"], options.PrimaryCities);
        Assert.Equal(["Kaštela"], options.FallbackCities);
        Assert.True(options.KauflandEnabled);
        Assert.True(options.DmEnabled);
        Assert.True(options.RibolaEnabled);
        Assert.True(options.MetroEnabled);
    }

    [Fact]
    public void FromConfiguration_UsesConfiguredTimeZoneAndRefreshSchedule()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["SPIZA_TIME_ZONE_ID"] = "UTC",
            ["SPIZA_REFRESH_DAILY_AT"] = "01:30",
            ["SPIZA_REFRESH_RETRY_INTERVAL_MINUTES"] = "20",
            ["SPIZA_REFRESH_RETRY_MAX_INTERVAL_MINUTES"] = "90"
        }).Build();

        var options = SpizaOptions.FromConfiguration(configuration);

        Assert.Equal("UTC", options.TimeZoneId);
        Assert.Equal(TimeZoneInfo.Utc, options.TimeZone);
        Assert.Equal(new TimeOnly(1, 30), options.RefreshDailyAt);
        Assert.Equal(20, options.RefreshRetryIntervalMinutes);
        Assert.Equal(90, options.RefreshRetryMaxIntervalMinutes);
    }

    [Fact]
    public void FromConfiguration_FallsBackForInvalidScheduleAndTimeZone()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["SPIZA_TIME_ZONE_ID"] = "Not/A_Time_Zone",
            ["SPIZA_REFRESH_DAILY_AT"] = "25:61",
            ["SPIZA_REFRESH_RETRY_INTERVAL_MINUTES"] = "30",
            ["SPIZA_REFRESH_RETRY_MAX_INTERVAL_MINUTES"] = "10"
        }).Build();

        var options = SpizaOptions.FromConfiguration(configuration);

        Assert.Equal("Europe/Zagreb", options.TimeZoneId);
        Assert.Equal(new TimeOnly(0, 5), options.RefreshDailyAt);
        Assert.Equal(30, options.RefreshRetryIntervalMinutes);
        Assert.Equal(30, options.RefreshRetryMaxIntervalMinutes);
    }

    [Fact]
    public void GetLocalDate_UsesCroatianCalendarDateAcrossUtcMidnightBoundary()
    {
        var timeZone = TimeZoneInfo.FindSystemTimeZoneById("Europe/Zagreb");
        var utc = new DateTimeOffset(2026, 10, 22, 22, 5, 0, TimeSpan.Zero);

        Assert.Equal(new DateTime(2026, 10, 23), SpizaOptions.GetLocalDate(utc, timeZone));
    }

    [Fact]
    public void FromConfiguration_ClampsOutOfRangePort()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["SPIZA_PORT"] = "77492"
        }).Build();

        var options = SpizaOptions.FromConfiguration(configuration);

        Assert.Equal(65535, options.Port);
    }

    [Fact]
    public void FromConfiguration_AllowsDisablingTheAdditionalRetailerSources()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["SPIZA_KAUFLAND_ENABLED"] = "false",
            ["SPIZA_DM_ENABLED"] = "false",
            ["SPIZA_RIBOLA_ENABLED"] = "false",
            ["SPIZA_METRO_ENABLED"] = "false"
        }).Build();

        var options = SpizaOptions.FromConfiguration(configuration);

        Assert.False(options.KauflandEnabled);
        Assert.False(options.DmEnabled);
        Assert.False(options.RibolaEnabled);
        Assert.False(options.MetroEnabled);
    }

    [Fact]
    public void FromConfiguration_UsesConfiguredArchiveExpansionLimitAndClampsIt()
    {
        var configured = SpizaOptions.FromConfiguration(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["SPIZA_SOURCE_ARCHIVE_MAX_EXPANDED_BYTES"] = "750000000"
            })
            .Build());
        var tooLarge = SpizaOptions.FromConfiguration(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["SPIZA_SOURCE_ARCHIVE_MAX_EXPANDED_BYTES"] = "9000000000"
            })
            .Build());

        Assert.Equal(750_000_000, configured.SourceArchiveMaxExpandedBytes);
        Assert.Equal(5_000_000_000, tooLarge.SourceArchiveMaxExpandedBytes);
    }

    [Fact]
    public void FromConfiguration_ParsesAndDeduplicatesConfiguredCitiesIgnoringAccentsAndCase()
    {
        var options = SpizaOptions.FromConfiguration(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["SPIZA_PRIMARY_CITIES"] = " Zagreb,Split, split, ,",
                ["SPIZA_FALLBACK_CITIES"] = "KASTELA,Kaštela,Pula"
            })
            .Build());

        Assert.Equal(["Zagreb", "Split"], options.PrimaryCities);
        Assert.Equal(["KASTELA", "Pula"], options.FallbackCities);
        Assert.Equal(SpizaOptions.NormalizeAreaName("Kaštela"),
            SpizaOptions.NormalizeAreaName("Kaštel Kambelovac"));
    }
}
