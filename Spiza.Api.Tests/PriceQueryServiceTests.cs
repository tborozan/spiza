using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Spiza.Api.Configuration;
using Spiza.Api.Data;
using Spiza.Api.Services;

namespace Spiza.Api.Tests;

public sealed class PriceQueryServiceTests
{
    private const string Barcode = "4006381333931";

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
    public async Task GetCurrentPricesAsync_TriesRequestedCityBeforeRequestFallbackAreas()
    {
        await using var database = await TestDatabase.CreateAsync();
        await database.SeedProductAsync();
        var today = DateTime.UtcNow.Date;
        await database.SeedPriceAsync("split", "Split", today, 2.49m);
        await database.SeedPriceAsync("solin", "Solin", today, 1.99m);

        var result = await database.Service.GetCurrentPricesAsync(Barcode, null, "Split", ["Solin"],
            null, null, CancellationToken.None);

        Assert.Equal("Split", result.MatchedArea);
        Assert.Equal("Split", Assert.Single(result.Prices).Store.City);
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
            today.AddDays(-1), today, CancellationToken.None);

        Assert.Equal("ok", result.Status);
        var row = Assert.Single(result.Prices);
        Assert.Equal("store-a", row.Store.StoreId);
        Assert.Equal(2.49m, row.RegularPrice);
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

        public static async Task<TestDatabase> CreateAsync(Dictionary<string, string?>? settings = null)
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
            var service = new PriceQueryService(factory, SpizaOptions.FromConfiguration(configuration), []);
            return new TestDatabase(connection, factory, service);
        }

        public async Task SeedProductAsync()
        {
            await using var db = await contextFactory.CreateDbContextAsync();
            db.Products.Add(new Product
            {
                Retailer = "lidl",
                ExternalProductId = "sku-1",
                Name = "Test chocolate",
                Brand = "Test brand",
                Barcode = Barcode,
                NormalizedBarcode = Barcode,
                Quantity = 0.5m,
                Unit = "kg",
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

        public async Task SeedProductWithPriceAsync(string city, decimal? promotionalPrice)
        {
            await SeedProductAsync();
            await SeedPriceAsync("store-a", city, DateTime.UtcNow.Date, 2.49m, promotionalPrice);
        }

        public async Task SeedPriceAsync(string storeId, string city, DateTime date, decimal regularPrice, decimal? promotionalPrice = null)
        {
            await using var db = await contextFactory.CreateDbContextAsync();
            var product = await db.Products.SingleAsync();
            var store = await db.Stores.SingleOrDefaultAsync(value => value.ExternalStoreId == storeId);
            if (store is null)
            {
                store = new Store
                {
                    Retailer = "lidl",
                    ExternalStoreId = storeId,
                    Name = $"Lidl {city}",
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
                UnitPrice = 4.98m,
                UnitPriceUnit = "kg",
                PromotionStart = promotionalPrice is null ? null : date.AddDays(-1),
                PromotionEnd = promotionalPrice is null ? null : date.AddDays(1),
                Currency = "EUR",
                RetrievedAt = DateTime.UtcNow,
                QualityFlagsJson = "[]"
            });
            await db.SaveChangesAsync();
        }

        public async ValueTask DisposeAsync() => await connection.DisposeAsync();
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
