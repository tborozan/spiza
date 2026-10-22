using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Spiza.Api.Data;
using Spiza.Api.Services;

namespace Spiza.Api.Tests;

public sealed class ReadinessServiceTests
{
    [Fact]
    public async Task CheckAsync_IsNotReadyWhenThereAreNoPriceObservations()
    {
        await using var database = await TestDatabase.CreateAsync();

        var state = await database.Service.CheckAsync(CancellationToken.None);

        Assert.True(state.DatabaseAvailable);
        Assert.False(state.HasPriceData);
        Assert.False(state.IsReady);
    }

    [Fact]
    public async Task CheckAsync_IsReadyOncePriceObservationsExist()
    {
        await using var database = await TestDatabase.CreateAsync();
        await database.AddPriceObservationAsync();

        var state = await database.Service.CheckAsync(CancellationToken.None);

        Assert.True(state.DatabaseAvailable);
        Assert.True(state.HasPriceData);
        Assert.True(state.IsReady);
    }

    private sealed class TestDatabase : IAsyncDisposable
    {
        private readonly SqliteConnection connection;
        private readonly TestContextFactory contextFactory;

        private TestDatabase(SqliteConnection connection, TestContextFactory contextFactory)
        {
            this.connection = connection;
            this.contextFactory = contextFactory;
            Service = new ReadinessService(contextFactory);
        }

        public ReadinessService Service { get; }

        public static async Task<TestDatabase> CreateAsync()
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var options = new DbContextOptionsBuilder<SpizaDbContext>().UseSqlite(connection).Options;
            var factory = new TestContextFactory(options);
            await using (var db = factory.CreateDbContext())
            {
                await db.Database.EnsureCreatedAsync();
            }

            return new TestDatabase(connection, factory);
        }

        public async Task AddPriceObservationAsync()
        {
            await using var db = await contextFactory.CreateDbContextAsync();
            var product = new Product
            {
                Retailer = "lidl",
                ExternalProductId = "sku-1",
                Name = "Test product",
                UpdatedAt = DateTime.UtcNow
            };
            var store = new Store
            {
                Retailer = "lidl",
                ExternalStoreId = "store-1",
                Name = "Lidl Split",
                City = "Split",
                UpdatedAt = DateTime.UtcNow
            };
            db.PriceObservations.Add(new PriceObservation
            {
                Product = product,
                Store = store,
                PriceDate = DateTime.UtcNow.Date,
                RegularPrice = 1.99m,
                EffectivePrice = 1.99m,
                Currency = "EUR",
                RetrievedAt = DateTime.UtcNow,
                QualityFlagsJson = "[]"
            });
            await db.SaveChangesAsync();
        }

        public ValueTask DisposeAsync() => connection.DisposeAsync();
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
