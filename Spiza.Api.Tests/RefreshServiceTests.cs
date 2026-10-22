using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Spiza.Api.Configuration;
using Spiza.Api.Data;
using Spiza.Api.Services;
using Spiza.Api.Sources;

namespace Spiza.Api.Tests;

public sealed class RefreshServiceTests
{
    [Fact]
    public async Task RefreshAsync_MarksOutdatedPricesStaleAndRefreshesOnlyStaleSources()
    {
        await using var database = await TestDatabase.CreateAsync();
        var options = SpizaOptions.FromConfiguration(new ConfigurationBuilder().Build());
        var lateSource = new MutablePriceSource("late-source", options.Today.AddDays(-1));
        var currentSource = new MutablePriceSource("current-source", options.Today);
        var sources = new IPublicPriceSource[] { lateSource, currentSource };
        var refresh = new RefreshService(database.Factory, sources, options,
            NullLogger<RefreshService>.Instance);

        var firstRun = await refresh.RefreshAsync();

        Assert.Equal("stale", Assert.Single(firstRun, result => result.Source == lateSource.Name).Status);
        Assert.Equal(options.Today.AddDays(-1), firstRun.Single(result => result.Source == lateSource.Name).LatestPriceDate);
        Assert.Equal("succeeded", firstRun.Single(result => result.Source == currentSource.Name).Status);

        lateSource.PriceDate = options.Today;
        var retry = await refresh.RefreshAsync(CancellationToken.None,
            new HashSet<string>([lateSource.Name], StringComparer.OrdinalIgnoreCase));

        var retryResult = Assert.Single(retry);
        Assert.Equal(lateSource.Name, retryResult.Source);
        Assert.Equal("succeeded", retryResult.Status);
        Assert.Equal(options.Today, retryResult.LatestPriceDate);
        Assert.Equal(1, currentSource.FetchCount);

        var queries = new PriceQueryService(database.Factory, options, sources);
        var coverage = await queries.GetCoverageAsync(CancellationToken.None);
        var retailerCoverage = Assert.Single(coverage.Sources, source => source.Retailer == lateSource.Name);
        Assert.Equal("succeeded", retailerCoverage.Status);
        Assert.Equal(options.Today, retailerCoverage.LatestPriceDate);
    }

    private sealed class MutablePriceSource(string name, DateTime priceDate) : IPublicPriceSource
    {
        public string Name => name;
        public bool Enabled => true;
        public int FetchCount { get; private set; }
        public DateTime PriceDate { get; set; } = priceDate;

        public Task<SourceFetchResult> FetchAsync(CancellationToken cancellationToken)
        {
            FetchCount++;
            var product = new SourceProduct("sku-1", "Test product", null, "4006381333931", null,
                1m, "kg", 2.49m, null, 2.49m, "kg", []);
            var store = new SourceStore("store-1", "Test store", "Main street 1", "21000", "Split");
            return Task.FromResult(new SourceFetchResult([new SourcePriceList(store, PriceDate, [product])], 0));
        }
    }

    private sealed class TestDatabase(SqliteConnection connection, TestContextFactory factory) : IAsyncDisposable
    {
        public TestContextFactory Factory { get; } = factory;

        public static async Task<TestDatabase> CreateAsync()
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var contextOptions = new DbContextOptionsBuilder<SpizaDbContext>().UseSqlite(connection).Options;
            var factory = new TestContextFactory(contextOptions);
            await using (var db = factory.CreateDbContext())
            {
                await db.Database.EnsureCreatedAsync();
            }

            return new TestDatabase(connection, factory);
        }

        public ValueTask DisposeAsync() => connection.DisposeAsync();
    }

    private sealed class TestContextFactory(DbContextOptions<SpizaDbContext> options) : IDbContextFactory<SpizaDbContext>
    {
        public SpizaDbContext CreateDbContext() => new(options);

        public Task<SpizaDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(CreateDbContext());
    }
}
