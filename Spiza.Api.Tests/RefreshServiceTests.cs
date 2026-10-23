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
        var initialRefreshState = new InitialRefreshState();
        var refresh = new RefreshService(database.Factory, sources, options,
            initialRefreshState, NullLogger<RefreshService>.Instance);

        var firstRun = await refresh.RefreshAsync();

        Assert.True(initialRefreshState.IsCompleted);
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

    [Fact]
    public async Task RefreshAsync_DoesNotMarkInitialRefreshCompleteForSubset()
    {
        await using var database = await TestDatabase.CreateAsync();
        var options = SpizaOptions.FromConfiguration(new ConfigurationBuilder().Build());
        var source = new MutablePriceSource("test-source", options.Today);
        var initialRefreshState = new InitialRefreshState();
        var refresh = new RefreshService(database.Factory, [source], options,
            initialRefreshState, NullLogger<RefreshService>.Instance);

        await refresh.RefreshAsync(CancellationToken.None,
            new HashSet<string>([source.Name], StringComparer.OrdinalIgnoreCase));

        Assert.False(initialRefreshState.IsCompleted);
    }

    [Fact]
    public async Task RefreshAsync_ImportsOnlyPrimaryCitiesWhenRetailerHasPrimaryStores()
    {
        await using var database = await TestDatabase.CreateAsync();
        var options = SpizaOptions.FromConfiguration(new ConfigurationBuilder().Build());
        var source = new MutablePriceSource("test-source", options.Today)
        {
            PriceLists =
            [
                CreateList("split-store", "Split"),
                CreateList("solin-store", "Solin"),
                CreateList("kastela-store", "Kastela"),
                CreateList("zagreb-store", "Zagreb"),
                CreateList("unknown-store", null)
            ]
        };
        var refresh = new RefreshService(database.Factory, [source], options, new InitialRefreshState(),
            NullLogger<RefreshService>.Instance);

        var result = Assert.Single(await refresh.RefreshAsync());

        Assert.Equal(2, result.StoreCount);
        await using var db = await database.Factory.CreateDbContextAsync();
        Assert.Equal(["solin-store", "split-store"], await db.Stores.AsNoTracking()
            .OrderBy(store => store.ExternalStoreId).Select(store => store.ExternalStoreId).ToArrayAsync());
        Assert.Equal(2, await db.PriceObservations.CountAsync());
    }

    [Fact]
    public async Task RefreshAsync_ImportsFallbackCitiesOnlyWhenRetailerHasNoPrimaryStore()
    {
        await using var database = await TestDatabase.CreateAsync();
        var options = SpizaOptions.FromConfiguration(new ConfigurationBuilder().Build());
        var source = new MutablePriceSource("test-source", options.Today)
        {
            PriceLists =
            [
                CreateList("kastela-store", "Kaštela"),
                CreateList("zagreb-store", "Zagreb")
            ]
        };
        var refresh = new RefreshService(database.Factory, [source], options, new InitialRefreshState(),
            NullLogger<RefreshService>.Instance);

        var result = Assert.Single(await refresh.RefreshAsync());

        Assert.Equal(1, result.StoreCount);
        await using var db = await database.Factory.CreateDbContextAsync();
        var store = await db.Stores.AsNoTracking().SingleAsync();
        Assert.Equal("kastela-store", store.ExternalStoreId);
        Assert.Equal("Kaštela", store.City);
    }

    [Fact]
    public async Task RefreshAsync_MatchesKaštelaFallbackToItsNamedSettlements()
    {
        await using var database = await TestDatabase.CreateAsync();
        var options = SpizaOptions.FromConfiguration(new ConfigurationBuilder().Build());
        var source = new MutablePriceSource("ribola", options.Today)
        {
            PriceLists = [CreateList("kastel-kambelovac", "Kaštel Kambelovac")]
        };
        var refresh = new RefreshService(database.Factory, [source], options, new InitialRefreshState(),
            NullLogger<RefreshService>.Instance);

        var result = Assert.Single(await refresh.RefreshAsync());

        Assert.Equal(1, result.StoreCount);
        await using var db = await database.Factory.CreateDbContextAsync();
        Assert.Equal("Kaštel Kambelovac", (await db.Stores.AsNoTracking().SingleAsync()).City);
    }

    [Fact]
    public async Task RefreshAsync_UsesFirstConfiguredFallbackCityWithAStore()
    {
        await using var database = await TestDatabase.CreateAsync();
        var options = SpizaOptions.FromConfiguration(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["SPIZA_FALLBACK_CITIES"] = "Trogir,Kaštela"
            })
            .Build());
        var source = new MutablePriceSource("test-source", options.Today)
        {
            PriceLists = [CreateList("kastela-store", "Kaštela"), CreateList("trogir-store", "Trogir")]
        };
        var refresh = new RefreshService(database.Factory, [source], options, new InitialRefreshState(),
            NullLogger<RefreshService>.Instance);

        await refresh.RefreshAsync();

        await using var db = await database.Factory.CreateDbContextAsync();
        var store = await db.Stores.AsNoTracking().SingleAsync();
        Assert.Equal("trogir-store", store.ExternalStoreId);
    }

    [Fact]
    public async Task RefreshAsync_UsesConfiguredPrimaryAndFallbackCities()
    {
        await using var database = await TestDatabase.CreateAsync();
        var options = SpizaOptions.FromConfiguration(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["SPIZA_PRIMARY_CITIES"] = "Zagreb",
                ["SPIZA_FALLBACK_CITIES"] = "Pula"
            })
            .Build());
        var source = new MutablePriceSource("test-source", options.Today)
        {
            PriceLists = [CreateList("zagreb-store", "ZAGREB"), CreateList("pula-store", "Pula")]
        };
        var refresh = new RefreshService(database.Factory, [source], options, new InitialRefreshState(),
            NullLogger<RefreshService>.Instance);

        await refresh.RefreshAsync();

        await using var db = await database.Factory.CreateDbContextAsync();
        var store = await db.Stores.AsNoTracking().SingleAsync();
        Assert.Equal("zagreb-store", store.ExternalStoreId);
    }

    [Fact]
    public async Task RefreshAsync_AppliesFallbackSelectionToStreamingSources()
    {
        await using var database = await TestDatabase.CreateAsync();
        var options = SpizaOptions.FromConfiguration(new ConfigurationBuilder().Build());
        var source = new StreamingPriceSource("streaming-source",
        [
            CreateList("kastela-store", "Kaštela"),
            CreateList("split-store", "Split")
        ]);
        var refresh = new RefreshService(database.Factory, [source], options, new InitialRefreshState(),
            NullLogger<RefreshService>.Instance);

        await refresh.RefreshAsync();

        await using var db = await database.Factory.CreateDbContextAsync();
        var store = await db.Stores.AsNoTracking().SingleAsync();
        Assert.Equal("split-store", store.ExternalStoreId);
    }

    [Fact]
    public async Task RefreshAsync_RetainsNationalPriceListsAlongsideLocalCityFiltering()
    {
        await using var database = await TestDatabase.CreateAsync();
        var options = SpizaOptions.FromConfiguration(new ConfigurationBuilder().Build());
        var source = new MutablePriceSource("dm", options.Today)
        {
            PriceLists = [
                CreateList("all", "National"),
                CreateList("zagreb-store", "Zagreb")
            ]
        };
        var refresh = new RefreshService(database.Factory, [source], options, new InitialRefreshState(),
            NullLogger<RefreshService>.Instance);

        var result = Assert.Single(await refresh.RefreshAsync());

        Assert.Equal(1, result.StoreCount);
        await using var db = await database.Factory.CreateDbContextAsync();
        var store = await db.Stores.AsNoTracking().SingleAsync();
        Assert.Equal("all", store.ExternalStoreId);
        Assert.Equal("National", store.City);
    }

    [Fact]
    public async Task RefreshAsync_DoesNotRewriteUnchangedStoreProductOrObservation()
    {
        await using var database = await TestDatabase.CreateAsync();
        var options = SpizaOptions.FromConfiguration(new ConfigurationBuilder().Build());
        var source = new MutablePriceSource("test-source", options.Today);
        var refresh = new RefreshService(database.Factory, [source], options, new InitialRefreshState(),
            NullLogger<RefreshService>.Instance);
        var sentinel = new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        await refresh.RefreshAsync();

        await using (var db = await database.Factory.CreateDbContextAsync())
        {
            (await db.Stores.SingleAsync()).UpdatedAt = sentinel;
            (await db.Products.SingleAsync()).UpdatedAt = sentinel;
            (await db.PriceObservations.SingleAsync()).RetrievedAt = sentinel;
            await db.SaveChangesAsync();
        }

        await refresh.RefreshAsync();

        await using var verifyDb = await database.Factory.CreateDbContextAsync();
        Assert.Equal(sentinel, (await verifyDb.Stores.AsNoTracking().SingleAsync()).UpdatedAt);
        Assert.Equal(sentinel, (await verifyDb.Products.AsNoTracking().SingleAsync()).UpdatedAt);
        Assert.Equal(sentinel, (await verifyDb.PriceObservations.AsNoTracking().SingleAsync()).RetrievedAt);
    }

    [Fact]
    public async Task RefreshAsync_MarksInitialRefreshCompleteAfterAllSourcesFinishEvenWhenOneFails()
    {
        await using var database = await TestDatabase.CreateAsync();
        var options = SpizaOptions.FromConfiguration(new ConfigurationBuilder().Build());
        var failedSource = new MutablePriceSource("failed-source", options.Today, shouldFail: true);
        var successfulSource = new MutablePriceSource("successful-source", options.Today);
        var initialRefreshState = new InitialRefreshState();
        var refresh = new RefreshService(database.Factory, [failedSource, successfulSource], options,
            initialRefreshState, NullLogger<RefreshService>.Instance);

        var results = await refresh.RefreshAsync();

        Assert.True(initialRefreshState.IsCompleted);
        Assert.Equal("failed", results.Single(result => result.Source == failedSource.Name).Status);
        Assert.Equal("succeeded", results.Single(result => result.Source == successfulSource.Name).Status);
        Assert.Equal(1, successfulSource.FetchCount);
    }

    [Fact]
    public async Task RefreshAsync_DoesNotFlagAValidBarcodeChangeOrLegacyUnitCanonicalization()
    {
        await using var database = await TestDatabase.CreateAsync();
        var options = SpizaOptions.FromConfiguration(new ConfigurationBuilder().Build());
        var source = new MutablePriceSource("lidl", options.Today)
        {
            Product = new SourceProduct("sku-1", "Čoksa čokolada banana-biskvit", null, "4006381333931", null,
                0.068m, "g", 1.39m, null, 20.44m, "g", [])
        };
        var refresh = new RefreshService(database.Factory, [source], options, new InitialRefreshState(),
            NullLogger<RefreshService>.Instance);

        await refresh.RefreshAsync();
        source.Product = new SourceProduct("sku-1", "Čoksa čokolada banana-biskvit", null, "3850102001606", null,
            0.068m, "kg", 1.39m, null, 20.44m, "kg", []);
        await refresh.RefreshAsync();

        var queries = new PriceQueryService(database.Factory, options, [source]);
        var result = await queries.GetCurrentPricesAsync("3850102001606", null, "Split", null,
            null, "lidl", CancellationToken.None);
        var price = Assert.Single(result.Prices);
        Assert.Equal("ok", result.Status);
        Assert.Equal("ok", price.Product.Status);
        Assert.Equal(0.068m, price.Product.Quantity);
        Assert.Equal("kg", price.Product.Unit);
        Assert.Equal("kg", price.UnitPriceUnit);
        Assert.Empty(price.QualityFlags);
    }

    private sealed class MutablePriceSource(string name, DateTime priceDate, bool shouldFail = false) : IPublicPriceSource
    {
        public string Name => name;
        public bool Enabled => true;
        public int FetchCount { get; private set; }
        public DateTime PriceDate { get; set; } = priceDate;
        public IReadOnlyList<SourcePriceList>? PriceLists { get; set; }
        public SourceProduct Product { get; set; } = new("sku-1", "Test product", null, "4006381333931", null,
            1m, "kg", 2.49m, null, 2.49m, "kg", []);

        public Task<SourceFetchResult> FetchAsync(CancellationToken cancellationToken)
        {
            FetchCount++;
            if (shouldFail)
            {
                throw new InvalidOperationException("Configured test failure.");
            }

            return Task.FromResult(new SourceFetchResult(PriceLists ?? [CreateList("store-1", "Split")], 0));
        }

        private SourcePriceList CreateList(string storeId, string? city) =>
            new(new SourceStore(storeId, $"Test store {storeId}", "Main street 1", "21000", city), PriceDate, [Product]);
    }

    private static SourcePriceList CreateList(string storeId, string? city) =>
        new(new SourceStore(storeId, $"Test store {storeId}", "Main street 1", "21000", city),
            DateTime.UtcNow.Date, [new SourceProduct("sku-1", "Test product", null, "4006381333931", null,
                1m, "kg", 2.49m, null, 2.49m, "kg", [])]);

    private sealed class StreamingPriceSource(string name, IReadOnlyList<SourcePriceList> priceLists) : IStreamingPublicPriceSource
    {
        public string Name => name;
        public bool Enabled => true;

        public Task<SourceFetchResult> FetchAsync(CancellationToken cancellationToken) =>
            throw new NotSupportedException("The test source uses streaming ingestion.");

        public async Task<int> FetchAndStreamAsync(
            Func<SourcePriceList, CancellationToken, Task> consume,
            CancellationToken cancellationToken)
        {
            foreach (var list in priceLists)
            {
                await consume(list, cancellationToken);
            }

            return 0;
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
