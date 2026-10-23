using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using System.Text.Json;
using Spiza.Api.Configuration;
using Spiza.Api.Data;
using Spiza.Api.Mcp;
using Spiza.Api.Services;
using Spiza.Api.Sources;

namespace Spiza.Api.Tests;

public sealed class PriceQueryServiceTests
{
    private const string Barcode = "4006381333931";
    private static readonly DateTime ArchiveDate = new(2026, 10, 22);

    [Fact]
    public async Task GetCurrentPricesAsync_UsesRequestFallbacksInOrderAndActivePromotionalPrice()
    {
        await using var database = await TestDatabase.CreateAsync();
        await database.SeedProductAsync();
        var today = DateTime.UtcNow.Date;
        await database.SeedPriceAsync("solin", "Solin", today, 2.49m, 1.99m);
        await database.SeedPriceAsync("kastela", "Kaštela", today, 2.29m, 1.49m);

        var result = await database.Service.GetCurrentPricesAsync(Barcode, null, "Split", ["Solin", "Kaštela"],
            null, null, CancellationToken.None);

        Assert.Equal("ok", result.Status);
        Assert.Equal("Solin", result.MatchedArea);
        var price = Assert.Single(result.Prices);
        Assert.Equal(2.49m, price.RegularPrice);
        Assert.Equal(1.99m, price.PromotionalPrice);
        Assert.Equal(1.99m, price.EffectivePrice);
        Assert.Equal("Solin", price.Store.City);
    }

    [Fact]
    public async Task GetCurrentPricesAsync_IncludesSplitAndSolinAsPrimaryAreas()
    {
        await using var database = await TestDatabase.CreateAsync();
        await database.SeedProductAsync();
        var today = DateTime.UtcNow.Date;
        await database.SeedPriceAsync("split", "Split", today, 2.49m);
        await database.SeedPriceAsync("solin", "Solin", today, 1.99m);

        var result = await database.Service.GetCurrentPricesAsync(Barcode, null, "Split", ["Solin"],
            null, null, CancellationToken.None);

        Assert.Contains(result.Prices, price => price.Store.City == "Split");
        Assert.Contains(result.Prices, price => price.Store.City == "Solin");
    }

    [Fact]
    public async Task GetCurrentPricesAsync_DistinguishesUnknownProductFromKnownProductWithoutObservation()
    {
        await using var database = await TestDatabase.CreateAsync();
        await database.SeedProductAsync();

        var unknown = await database.Service.GetCurrentPricesAsync("9999999999999", null, null, null, null, null, CancellationToken.None);
        var knownWithoutPrice = await database.Service.GetCurrentPricesAsync(Barcode, null, null, null, null, null, CancellationToken.None);

        Assert.Equal("unknown_product", unknown.Status);
        Assert.Equal("incomplete_data", knownWithoutPrice.Status);
    }

    [Fact]
    public async Task GetCurrentPricesAsync_LeavesValidEurPriceUsableWhenOnlyUnitMetadataIsInconsistent()
    {
        await using var database = await TestDatabase.CreateAsync();
        await database.SeedProductAsync();
        await database.SeedPriceAsync("store-a", "Split", ArchiveDate, 2.49m,
            qualityFlags: "[\"unit_inconsistent\"]");

        var result = await database.Service.GetCurrentPricesAsync(Barcode, null, "Split", null,
            null, null, CancellationToken.None);

        Assert.Equal("incomplete_data", result.Status);
        var price = Assert.Single(result.Prices);
        Assert.Equal(2.49m, price.EffectivePrice);
        Assert.True(price.EffectivePriceUsable);
        Assert.Equal("EUR", price.Currency);
        Assert.Equal("incomplete_data", price.Status);
        Assert.Contains("unit_inconsistent", price.QualityFlags);

        using var mcp = JsonDocument.Parse(await SpizaMcpTools.CurrentPrices(database.Service,
            barcode: Barcode, city: "Split"));
        var mcpPrice = mcp.RootElement.GetProperty("prices")[0];
        Assert.Equal("incomplete_data", mcp.RootElement.GetProperty("status").GetString());
        Assert.True(mcpPrice.GetProperty("effectivePriceUsable").GetBoolean());
        Assert.Equal(2.49m, mcpPrice.GetProperty("effectivePrice").GetDecimal());
    }

    [Fact]
    public async Task GetCurrentPricesAsync_DoesNotTreatMissingPriceAsUsableBecauseUnitPriceExists()
    {
        await using var database = await TestDatabase.CreateAsync();
        await database.SeedProductAsync();
        await database.SeedPriceAsync("store-a", "Split", ArchiveDate, null,
            qualityFlags: "[\"price_missing\"]");

        var result = await database.Service.GetCurrentPricesAsync(Barcode, null, "Split", null,
            null, null, CancellationToken.None);

        Assert.Equal("incomplete_data", result.Status);
        var price = Assert.Single(result.Prices);
        Assert.Null(price.EffectivePrice);
        Assert.False(price.EffectivePriceUsable);
        Assert.Contains("price_missing", price.QualityFlags);
    }

    [Fact]
    public async Task GetCurrentPricesAsync_ReturnsNoLocalPriceWhenProductHasPricesOutsideRequestedCity()
    {
        await using var database = await TestDatabase.CreateAsync();
        await database.SeedProductWithPriceAsync(city: "Split", promotionalPrice: null);

        var result = await database.Service.GetCurrentPricesAsync(Barcode, null, "Zagreb", null, null, null, CancellationToken.None);

        Assert.Equal("no_local_price", result.Status);
        Assert.Empty(result.Prices);
    }

    [Fact]
    public async Task GetBatchPricesAsync_ReturnsOneResultForEachInputBarcode()
    {
        await using var database = await TestDatabase.CreateAsync();
        await database.SeedProductWithPriceAsync(city: "Zagreb", promotionalPrice: null);

        var results = await database.Service.GetBatchPricesAsync([Barcode, "9999999999999", Barcode], null, null, CancellationToken.None);

        Assert.Equal(3, results.Count);
        Assert.Equal("ok", results[0].Result.Status);
        Assert.Equal("unknown_product", results[1].Result.Status);
        Assert.Equal("ok", results[2].Result.Status);
    }

    [Fact]
    public async Task GetBatchPricesAsync_AppliesRequestFallbackAreas()
    {
        await using var database = await TestDatabase.CreateAsync();
        await database.SeedProductWithPriceAsync(city: "Solin", promotionalPrice: null);

        var results = await database.Service.GetBatchPricesAsync([Barcode], "Split", ["Solin", "Kaštela"], CancellationToken.None);

        var result = Assert.Single(results).Result;
        Assert.Equal("ok", result.Status);
        Assert.Equal("Solin", result.MatchedArea);
    }

    [Fact]
    public async Task GetCurrentPricesForExportAsync_AppliesRequestFallbackAreas()
    {
        await using var database = await TestDatabase.CreateAsync();
        await database.SeedProductWithPriceAsync(city: "Solin", promotionalPrice: null);

        var prices = await database.Service.GetCurrentPricesForExportAsync("Split", null, ["Solin", "Kaštela"],
            CancellationToken.None);

        Assert.Equal("Solin", Assert.Single(prices).Store.City);
    }

    [Fact]
    public async Task SearchProductsAsync_FlagsDuplicateProductsForTheSameRetailerAndBarcode()
    {
        await using var database = await TestDatabase.CreateAsync();
        await database.SeedProductAsync();
        await database.SeedDuplicateProductAsync();

        var products = await database.Service.SearchProductsAsync(null, Barcode, null, null, CancellationToken.None);

        Assert.Equal(2, products.Count);
        Assert.All(products, product => Assert.Contains("duplicate_product", product.QualityFlags));
    }

    [Fact]
    public async Task GetPriceHistoryAsync_FiltersByStoreAndInclusiveDateRange()
    {
        await using var database = await TestDatabase.CreateAsync();
        var today = DateTime.UtcNow.Date;
        await database.SeedProductAsync();
        await database.SeedPriceAsync("store-a", "Zagreb", today.AddDays(-2), 2.00m);
        await database.SeedPriceAsync("store-a", "Zagreb", today, 2.49m);
        await database.SeedPriceAsync("store-b", "Split", today, 3.00m);

        var result = await database.Service.GetPriceHistoryAsync(Barcode, "store-a", null,
            today.AddDays(-1), today, null, null, CancellationToken.None);

        Assert.Equal("ok", result.Status);
        var row = Assert.Single(result.Prices);
        Assert.Equal("store-a", row.Store.StoreId);
        Assert.Equal(2.49m, row.RegularPrice);
    }

    [Fact]
    public async Task GetCurrentPricesAsync_ReturnsBothArchiveProductsWithoutBarcodeOrUnitWarnings()
    {
        await using var database = await TestDatabase.CreateAsync();
        const string chocolateBarcode = "3850102001606";
        const string pateBarcode = "3858881221543";
        await database.SeedProductAsync("lidl", chocolateBarcode, "Čoksa čokolada banana-biskvit", 0.068m, "kg");
        await database.SeedProductAsync("lidl", pateBarcode, "Gavrilović Jetrena pašteta, 100g", 0.1m, "kg");
        await database.SeedPriceAsync("130", "Split", ArchiveDate, 1.39m, retailer: "lidl", barcode: chocolateBarcode,
            unitPrice: 20.44m, unitPriceUnit: "kg");
        await database.SeedPriceAsync("155", "Split", ArchiveDate, 1.39m, retailer: "lidl", barcode: chocolateBarcode,
            unitPrice: 20.44m, unitPriceUnit: "kg");
        await database.SeedPriceAsync("130", "Split", ArchiveDate, 1.39m, retailer: "lidl", barcode: pateBarcode,
            unitPrice: 13.9m, unitPriceUnit: "kg");

        var chocolate = await database.Service.GetCurrentPricesAsync(chocolateBarcode, null, "Split", null,
            null, null, CancellationToken.None);
        var pate = await database.Service.GetCurrentPricesAsync(pateBarcode, null, "Split", null,
            null, null, CancellationToken.None);
        var searchedPate = await database.Service.SearchProductsAsync("Jetrena pašteta", null, "lidl", null, CancellationToken.None);
        using var chocolateMcp = JsonDocument.Parse(await SpizaMcpTools.CurrentPrices(database.Service,
            barcode: chocolateBarcode, city: "Split"));
        using var pateMcp = JsonDocument.Parse(await SpizaMcpTools.CurrentPrices(database.Service,
            barcode: pateBarcode, city: "Split"));

        Assert.Equal("ok", chocolate.Status);
        Assert.Equal(2, chocolate.Prices.Count);
        Assert.All(chocolate.Prices, price =>
        {
            Assert.Equal(1.39m, price.EffectivePrice);
            Assert.Equal(0.068m, price.Product.Quantity);
            Assert.Equal("kg", price.Product.Unit);
            Assert.Equal(20.44m, price.UnitPrice);
            Assert.Equal("kg", price.UnitPriceUnit);
            Assert.Empty(price.QualityFlags);
        });
        Assert.Equal("ok", pate.Status);
        var patePrice = Assert.Single(pate.Prices);
        Assert.Equal(1.39m, patePrice.EffectivePrice);
        Assert.Equal("ok", patePrice.Product.Status);
        Assert.Empty(patePrice.QualityFlags);
        Assert.Equal("ok", Assert.Single(searchedPate).Status);
        Assert.Empty(Assert.Single(searchedPate).QualityFlags);
        Assert.Equal("ok", chocolateMcp.RootElement.GetProperty("status").GetString());
        Assert.Equal(2, chocolateMcp.RootElement.GetProperty("prices").GetArrayLength());
        Assert.Equal("ok", pateMcp.RootElement.GetProperty("status").GetString());
        Assert.Empty(pateMcp.RootElement.GetProperty("prices")[0].GetProperty("qualityFlags").EnumerateArray());
    }

    [Fact]
    public async Task GetCurrentPricesAsync_UsesKaštelaOnlyForRetailersWithoutSplitOrSolinPricesAndAlwaysIncludesNationalDm()
    {
        await using var database = await TestDatabase.CreateAsync();
        foreach (var retailer in new[] { "lidl", "konzum", "dm" })
        {
            await database.SeedProductAsync(retailer, Barcode, $"{retailer} product");
        }

        await database.SeedPriceAsync("130", "Split", ArchiveDate, 1.39m, retailer: "lidl");
        await database.SeedPriceAsync("155", "Solin", ArchiveDate, 1.39m, retailer: "lidl");
        await database.SeedPriceAsync("200", "Kaštela", ArchiveDate, 1.29m, retailer: "lidl");
        await database.SeedPriceAsync("konzum-ka", "Kaštela", ArchiveDate, 1.49m, retailer: "konzum");
        await database.SeedPriceAsync("all", "National", ArchiveDate, 1.59m, retailer: "dm");

        var result = await database.Service.GetCurrentPricesAsync(Barcode, null, "Split", ["Kaštela"],
            null, null, CancellationToken.None);

        Assert.Equal("ok", result.Status);
        Assert.Equal(4, result.Prices.Count);
        Assert.Contains(result.Prices, price => price.Store.Retailer == "lidl" && price.Store.City == "Split");
        Assert.Contains(result.Prices, price => price.Store.Retailer == "lidl" && price.Store.City == "Solin");
        Assert.DoesNotContain(result.Prices, price => price.Store.Retailer == "lidl" && price.Store.City == "Kaštela");
        Assert.Contains(result.Prices, price => price.Store.Retailer == "konzum" && price.Store.City == "Kaštela");
        Assert.Contains(result.Prices, price => price.Store.Retailer == "dm" && price.Store.City == "National");
        var coverage = await database.Service.GetCoverageAsync(CancellationToken.None);
        Assert.Contains(coverage.Areas, area => area.Retailer == "dm" && area.City == "National");
    }

    [Fact]
    public async Task GetPriceHistoryAsync_FiltersToPrimaryAndPerRetailerFallbackAreas()
    {
        await using var database = await TestDatabase.CreateAsync();
        foreach (var retailer in new[] { "lidl", "konzum", "dm" })
        {
            await database.SeedProductAsync(retailer, Barcode, $"{retailer} product");
        }

        await database.SeedPriceAsync("130", "Split", ArchiveDate.AddDays(-1), 1.39m, retailer: "lidl");
        await database.SeedPriceAsync("155", "Solin", ArchiveDate, 1.39m, retailer: "lidl");
        await database.SeedPriceAsync("200", "Kaštela", ArchiveDate, 1.29m, retailer: "lidl");
        await database.SeedPriceAsync("konzum-ka", "Kaštela", ArchiveDate, 1.49m, retailer: "konzum");
        await database.SeedPriceAsync("all", "National", ArchiveDate, 1.59m, retailer: "dm");

        var result = await database.Service.GetPriceHistoryAsync(Barcode, null, null, ArchiveDate.AddDays(-1), ArchiveDate,
            "Split", ["Kaštela"], CancellationToken.None);

        Assert.Equal("ok", result.Status);
        Assert.Equal(4, result.Prices.Count);
        Assert.Contains(result.Prices, price => price.Store.Retailer == "lidl" && price.Store.City == "Split");
        Assert.Contains(result.Prices, price => price.Store.Retailer == "lidl" && price.Store.City == "Solin");
        Assert.DoesNotContain(result.Prices, price => price.Store.Retailer == "lidl" && price.Store.City == "Kaštela");
        Assert.Contains(result.Prices, price => price.Store.Retailer == "konzum" && price.Store.City == "Kaštela");
        Assert.Contains(result.Prices, price => price.Store.Retailer == "dm" && price.Store.City == "National");
    }

    [Fact]
    public async Task GetCoverageAsync_ReportsObservationCountsSeparatelyFromPendingFailedPartialAndRunningRefreshes()
    {
        var sources = new IPublicPriceSource[]
        {
            new TestSource("pending"), new TestSource("failed"), new TestSource("partial"), new TestSource("lidl")
        };
        await using var database = await TestDatabase.CreateAsync(sources: sources);
        await database.SeedProductAsync("partial", Barcode, "Partial product");
        await database.SeedProductAsync("lidl", Barcode, "Running product");
        await database.SeedPriceAsync("partial-store", "Split", ArchiveDate, 1.39m, retailer: "partial");
        await database.SeedPriceAsync("130", "Split", ArchiveDate, 1.39m, retailer: "lidl");
        await database.SeedRunAsync("failed", "failed", ArchiveDate.AddDays(1), "request failed");
        await database.SeedRunAsync("partial", "partial", ArchiveDate.AddDays(1), "2 public price list(s) could not be read.");
        await database.SeedRunAsync("lidl", "running", ArchiveDate.AddDays(1), null, completed: false);

        var result = await database.Service.GetCoverageAsync(CancellationToken.None);
        var pending = Assert.Single(result.Sources, source => source.Retailer == "pending");
        var failed = Assert.Single(result.Sources, source => source.Retailer == "failed");
        var partial = Assert.Single(result.Sources, source => source.Retailer == "partial");
        var running = Assert.Single(result.Sources, source => source.Retailer == "lidl");

        Assert.Equal("pending", pending.Status);
        Assert.Equal(0, pending.StoreCount);
        Assert.Equal("failed", failed.Status);
        Assert.Equal(0, failed.StoreCount);
        Assert.Equal("partial", partial.Status);
        Assert.Equal(1, partial.StoreCount);
        Assert.Equal(1, partial.ProductCount);
        Assert.Equal(2, partial.FailedLists);
        Assert.Null(partial.LastSuccess);
        Assert.Equal("running", running.Status);
        Assert.Equal(1, running.StoreCount);
        Assert.Equal(1, running.ProductCount);
        Assert.Equal(ArchiveDate, running.LatestPriceDate);

        var validRow = await database.Service.GetCurrentPricesAsync(Barcode, null, "Split", null,
            null, "lidl", CancellationToken.None);
        Assert.Equal("ok", validRow.Status);
        Assert.Equal("running", running.Status);
    }

    [Fact]
    public async Task GetCurrentPricesAsync_DoesNotTurnTextQueryIntoBarcodeSubstringFallback()
    {
        await using var database = await TestDatabase.CreateAsync();
        await database.SeedProductWithPriceAsync("Split", null);

        var result = await database.Service.GetCurrentPricesAsync("prefix-" + Barcode, "Test chocolate", "Split", null,
            null, null, CancellationToken.None);

        Assert.Equal("unknown_product", result.Status);
        Assert.Empty(result.Prices);
    }

    private sealed class TestDatabase : IAsyncDisposable
    {
        private readonly SqliteConnection connection;
        private readonly TestContextFactory contextFactory;

        private TestDatabase(SqliteConnection connection, TestContextFactory contextFactory, PriceQueryService service)
        {
            this.connection = connection;
            this.contextFactory = contextFactory;
            Service = service;
        }

        public PriceQueryService Service { get; }

        public static async Task<TestDatabase> CreateAsync(Dictionary<string, string?>? settings = null,
            IEnumerable<IPublicPriceSource>? sources = null)
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var contextOptions = new DbContextOptionsBuilder<SpizaDbContext>().UseSqlite(connection).Options;
            var factory = new TestContextFactory(contextOptions);
            await using (var db = factory.CreateDbContext())
            {
                await db.Database.EnsureCreatedAsync();
            }

            var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings ?? new Dictionary<string, string?>()).Build();
            var service = new PriceQueryService(factory, SpizaOptions.FromConfiguration(configuration), sources ?? []);
            return new TestDatabase(connection, factory, service);
        }

        public async Task SeedProductAsync(string retailer = "lidl", string barcode = Barcode,
            string name = "Test chocolate", decimal? quantity = 0.5m, string? unit = "kg")
        {
            await using var db = await contextFactory.CreateDbContextAsync();
            db.Products.Add(new Product
            {
                Retailer = retailer,
                ExternalProductId = $"sku-{barcode}",
                Name = name,
                Brand = "Test brand",
                Barcode = barcode,
                NormalizedBarcode = barcode,
                Quantity = quantity,
                Unit = unit,
                UpdatedAt = DateTime.UtcNow
            });
            await db.SaveChangesAsync();
        }

        public async Task SeedDuplicateProductAsync()
        {
            await using var db = await contextFactory.CreateDbContextAsync();
            db.Products.Add(new Product
            {
                Retailer = "lidl",
                ExternalProductId = "sku-duplicate",
                Name = "Duplicate chocolate listing",
                Barcode = Barcode,
                NormalizedBarcode = Barcode,
                UpdatedAt = DateTime.UtcNow
            });
            await db.SaveChangesAsync();
        }

        public async Task SeedProductWithPriceAsync(string city, decimal? promotionalPrice, string retailer = "lidl", string barcode = Barcode)
        {
            await SeedProductAsync(retailer, barcode);
            await SeedPriceAsync("store-a", city, DateTime.UtcNow.Date, 2.49m, promotionalPrice, retailer, barcode);
        }

        public async Task SeedPriceAsync(string storeId, string city, DateTime date, decimal? regularPrice,
            decimal? promotionalPrice = null, string retailer = "lidl", string barcode = Barcode,
            decimal? unitPrice = 4.98m, string? unitPriceUnit = "kg", string qualityFlags = "[]")
        {
            await using var db = await contextFactory.CreateDbContextAsync();
            var product = await db.Products.SingleAsync(value => value.Retailer == retailer && value.NormalizedBarcode == barcode);
            var store = await db.Stores.SingleOrDefaultAsync(value => value.Retailer == retailer && value.ExternalStoreId == storeId);
            if (store is null)
            {
                store = new Store
                {
                    Retailer = retailer,
                    ExternalStoreId = storeId,
                    Name = $"{retailer} {city}",
                    City = city,
                    UpdatedAt = DateTime.UtcNow
                };
                db.Stores.Add(store);
            }

            db.PriceObservations.Add(new PriceObservation
            {
                Product = product,
                Store = store,
                PriceDate = date,
                RegularPrice = regularPrice,
                PromotionalPrice = promotionalPrice,
                EffectivePrice = promotionalPrice ?? regularPrice,
                UnitPrice = unitPrice,
                UnitPriceUnit = unitPriceUnit,
                PromotionStart = promotionalPrice is null ? null : date.AddDays(-1),
                PromotionEnd = promotionalPrice is null ? null : date.AddDays(1),
                Currency = "EUR",
                RetrievedAt = DateTime.UtcNow,
                QualityFlagsJson = qualityFlags
            });
            await db.SaveChangesAsync();
        }

        public async Task SeedRunAsync(string source, string status, DateTime startedAt, string? error, bool completed = true)
        {
            await using var db = await contextFactory.CreateDbContextAsync();
            db.SourceRuns.Add(new SourceRun
            {
                Source = source,
                Status = status,
                StartedAt = startedAt,
                CompletedAt = completed ? startedAt.AddMinutes(1) : null,
                Error = error
            });
            await db.SaveChangesAsync();
        }

        public async ValueTask DisposeAsync() => await connection.DisposeAsync();
    }

    private sealed class TestSource(string name) : IPublicPriceSource
    {
        public string Name => name;
        public bool Enabled => true;
        public Task<SourceFetchResult> FetchAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new SourceFetchResult([], 0));
    }

    private sealed class TestContextFactory(DbContextOptions<SpizaDbContext> options) : IDbContextFactory<SpizaDbContext>
    {
        public SpizaDbContext CreateDbContext() => new(options);

        public Task<SpizaDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(CreateDbContext());
        }
    }
}
