using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Spiza.Api.Configuration;
using Spiza.Api.Data;
using Spiza.Api.Sources;

namespace Spiza.Api.Services;

public sealed record RefreshSourceResult(
    string Source,
    string Status,
    int StoreCount,
    int ProductCount,
    int FailedLists,
    string? Error,
    DateTime? LatestPriceDate = null);

public sealed class RefreshService(
    IDbContextFactory<SpizaDbContext> dbFactory,
    IEnumerable<IPublicPriceSource> sources,
    SpizaOptions options,
    ILogger<RefreshService> logger)
{
    private static readonly SemaphoreSlim RefreshLock = new(1, 1);

    public void StartBackgroundRefresh()
    {
        _ = Task.Run(async () =>
        {
            try
            {
                await RefreshAsync(CancellationToken.None);
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Background source refresh failed before completion.");
            }
        });
    }

    public async Task<IReadOnlyList<RefreshSourceResult>> RefreshAsync(
        CancellationToken cancellationToken = default,
        IReadOnlySet<string>? sourceNames = null)
    {
        if (!await RefreshLock.WaitAsync(0, cancellationToken))
        {
            return [new RefreshSourceResult("all", "already_running", 0, 0, 0, null)];
        }

        try
        {
            var results = new List<RefreshSourceResult>();
            foreach (var source in sources)
            {
                if (sourceNames is not null && !sourceNames.Contains("all") && !sourceNames.Contains(source.Name))
                {
                    continue;
                }

                if (!source.Enabled)
                {
                    results.Add(new RefreshSourceResult(source.Name, "disabled", 0, 0, 0, null));
                    continue;
                }

                results.Add(await RefreshOneAsync(source, cancellationToken));
            }

            return results;
        }
        finally
        {
            RefreshLock.Release();
        }
    }

    private async Task<RefreshSourceResult> RefreshOneAsync(IPublicPriceSource source, CancellationToken cancellationToken)
    {
        var startedAt = DateTime.UtcNow;
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var run = new SourceRun
        {
            Source = source.Name,
            StartedAt = startedAt,
            Status = "running"
        };
        db.SourceRuns.Add(run);
        await db.SaveChangesAsync(cancellationToken);

        try
        {
            var result = await source.FetchAsync(cancellationToken);
            var storeCount = 0;
            var productCount = 0;
            var latestPriceDate = result.PriceLists.Count == 0
                ? (DateTime?)null
                : result.PriceLists.Max(list => list.PriceDate.Date);
            var today = options.Today;
            var hasOutdatedLists = result.PriceLists.Count == 0 || result.PriceLists.Any(list => list.PriceDate.Date < today);

            foreach (var list in result.PriceLists)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var store = await UpsertStoreAsync(db, source.Name, list.Store, cancellationToken);
                storeCount++;
                var productIds = list.Products.Select(product => product.ExternalProductId).Distinct(StringComparer.Ordinal).ToArray();
                var productMap = await LoadProductsAsync(db, source.Name, productIds, cancellationToken);
                var importFlags = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);

                foreach (var imported in list.Products)
                {
                    var barcode = NormalizeBarcode(imported.Barcode);
                    if (!productMap.TryGetValue(imported.ExternalProductId, out var product))
                    {
                        product = new Product
                        {
                            Retailer = source.Name,
                            ExternalProductId = imported.ExternalProductId,
                            Name = imported.Name.Trim(),
                            UpdatedAt = DateTime.UtcNow
                        };
                        productMap.Add(imported.ExternalProductId, product);
                        db.Products.Add(product);
                    }

                    var flags = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    if (NormalizeBarcode(product.Barcode) is { } previousBarcode && barcode is not null && previousBarcode != barcode)
                    {
                        flags.Add("barcode_mismatch");
                    }

                    if (product.Quantity is not null && imported.Quantity is not null &&
                        product.Unit is not null && imported.Unit is not null &&
                        !AreEquivalentQuantities(product.Quantity.Value, product.Unit, imported.Quantity.Value, imported.Unit))
                    {
                        flags.Add("unit_inconsistent");
                    }

                    if (!importFlags.TryGetValue(imported.ExternalProductId, out var accumulatedFlags))
                    {
                        importFlags[imported.ExternalProductId] = flags;
                    }
                    else
                    {
                        accumulatedFlags.UnionWith(flags);
                    }

                    product.Name = imported.Name.Trim();
                    product.Brand = Clean(imported.Brand);
                    product.Barcode = Clean(imported.Barcode);
                    product.NormalizedBarcode = barcode;
                    product.Category = Clean(imported.Category);
                    product.Quantity = imported.Quantity;
                    product.Unit = Clean(imported.Unit);
                    product.UpdatedAt = DateTime.UtcNow;
                }

                foreach (var duplicate in list.Products
                             .Where(product => NormalizeBarcode(product.Barcode) is not null)
                             .GroupBy(product => NormalizeBarcode(product.Barcode)!, StringComparer.Ordinal)
                             .Where(group => group.Select(product => product.ExternalProductId).Distinct(StringComparer.Ordinal).Count() > 1)
                             .SelectMany(group => group))
                {
                    importFlags[duplicate.ExternalProductId].Add("duplicate_product");
                }

                await db.SaveChangesAsync(cancellationToken);
                productCount += productMap.Count;

                var productById = productMap.Values.ToDictionary(product => product.ExternalProductId, product => product);
                var productDatabaseIds = productById.Values.Select(product => product.Id).ToArray();
                var date = list.PriceDate.Date;
                var existingPrices = await db.PriceObservations
                    .Where(price => price.StoreId == store.Id && price.PriceDate == date && productDatabaseIds.Contains(price.ProductId))
                    .ToDictionaryAsync(price => price.ProductId, cancellationToken);

                foreach (var imported in list.Products)
                {
                    var product = productById[imported.ExternalProductId];
                    var flags = imported.QualityFlags.ToList();
                    flags.AddRange(importFlags[imported.ExternalProductId]);
                    if (list.Store.City is null)
                    {
                        flags.Add("store_location_missing");
                    }

                    var effective = ResolveEffectivePrice(imported.RegularPrice, imported.PromotionalPrice, list.PriceDate);
                    if (!existingPrices.TryGetValue(product.Id, out var observation))
                    {
                        observation = new PriceObservation
                        {
                            ProductId = product.Id,
                            StoreId = store.Id,
                            PriceDate = date,
                            RetrievedAt = DateTime.UtcNow
                        };
                        existingPrices.Add(product.Id, observation);
                        db.PriceObservations.Add(observation);
                    }

                    observation.RegularPrice = imported.RegularPrice;
                    observation.PromotionalPrice = imported.PromotionalPrice;
                    observation.EffectivePrice = effective;
                    observation.UnitPrice = imported.UnitPrice;
                    observation.UnitPriceUnit = imported.UnitPriceUnit;
                    observation.Currency = "EUR";
                    observation.RetrievedAt = DateTime.UtcNow;
                    observation.QualityFlagsJson = JsonSerializer.Serialize(flags.Distinct(StringComparer.OrdinalIgnoreCase));
                }

                await db.SaveChangesAsync(cancellationToken);
            }

            run.CompletedAt = DateTime.UtcNow;
            run.StoreCount = storeCount;
            run.ProductCount = productCount;
            run.Status = result.FailedLists > 0
                ? "partial"
                : hasOutdatedLists
                    ? "stale"
                    : "succeeded";
            if (result.FailedLists > 0)
            {
                run.Error = $"{result.FailedLists} public price list(s) could not be read.";
            }
            else if (run.Status == "stale")
            {
                run.Error = $"One or more public price lists are older than the current local date {today:yyyy-MM-dd}.";
            }

            await PruneHistoryAsync(db, cancellationToken);
            await db.SaveChangesAsync(cancellationToken);
            logger.LogInformation("Source refresh {Source} finished with {Status}: {Stores} stores, {Products} product rows, {Failures} failed lists, latest price date {PriceDate}.",
                source.Name, run.Status, storeCount, productCount, result.FailedLists, latestPriceDate);
            return new RefreshSourceResult(source.Name, run.Status, storeCount, productCount, result.FailedLists, run.Error, latestPriceDate);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            run.CompletedAt = DateTime.UtcNow;
            run.Status = "cancelled";
            await db.SaveChangesAsync(CancellationToken.None);
            throw;
        }
        catch (Exception exception)
        {
            run.CompletedAt = DateTime.UtcNow;
            run.Status = "failed";
            run.Error = exception.Message.Length > 1000 ? exception.Message[..1000] : exception.Message;
            await db.SaveChangesAsync(CancellationToken.None);
            logger.LogError(exception, "Public price source {Source} failed.", source.Name);
            return new RefreshSourceResult(source.Name, run.Status, run.StoreCount, run.ProductCount, 0, run.Error);
        }
    }

    private static async Task<Store> UpsertStoreAsync(SpizaDbContext db, string retailer, SourceStore imported, CancellationToken cancellationToken)
    {
        var store = await db.Stores.FirstOrDefaultAsync(value => value.Retailer == retailer && value.ExternalStoreId == imported.ExternalStoreId, cancellationToken);
        if (store is null)
        {
            store = new Store { Retailer = retailer, ExternalStoreId = imported.ExternalStoreId, Name = imported.Name };
            db.Stores.Add(store);
        }

        store.Name = imported.Name;
        store.Address = imported.Address;
        store.PostalCode = imported.PostalCode;
        store.City = imported.City;
        store.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        return store;
    }

    private static async Task<Dictionary<string, Product>> LoadProductsAsync(
        SpizaDbContext db,
        string retailer,
        string[] externalIds,
        CancellationToken cancellationToken)
    {
        var map = new Dictionary<string, Product>(StringComparer.Ordinal);
        foreach (var chunk in externalIds.Chunk(400))
        {
            var values = await db.Products
                .Where(product => product.Retailer == retailer && chunk.Contains(product.ExternalProductId))
                .ToListAsync(cancellationToken);
            foreach (var product in values)
            {
                map[product.ExternalProductId] = product;
            }
        }

        return map;
    }

    private async Task PruneHistoryAsync(SpizaDbContext db, CancellationToken cancellationToken)
    {
        if (options.HistoryRetentionDays <= 0)
        {
            return;
        }

        var cutoff = options.Today.AddDays(-options.HistoryRetentionDays);
        await db.PriceObservations.Where(price => price.PriceDate < cutoff).ExecuteDeleteAsync(cancellationToken);
    }

    private decimal? ResolveEffectivePrice(decimal? regular, decimal? promotional, DateTime priceDate)
    {
        if (options.EffectivePriceRule == "regular")
        {
            return regular;
        }

        return promotional ?? regular;
    }

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string? NormalizeBarcode(string? barcode)
    {
        if (string.IsNullOrWhiteSpace(barcode))
        {
            return null;
        }

        var digits = new string(barcode.Where(char.IsDigit).ToArray());
        return digits.Length == 0 ? null : digits;
    }

    private static bool AreEquivalentQuantities(decimal firstQuantity, string firstUnit, decimal secondQuantity, string secondUnit)
    {
        static (string Dimension, decimal Scale)? UnitDetails(string unit) => unit.Trim().ToLowerInvariant() switch
        {
            "kg" => ("mass", 1000m),
            "g" => ("mass", 1m),
            "l" => ("volume", 1000m),
            "ml" => ("volume", 1m),
            "piece" or "pcs" or "kom" or "komad" or "komada" => ("count", 1m),
            _ => null
        };

        var first = UnitDetails(firstUnit);
        var second = UnitDetails(secondUnit);
        if (first is null || second is null || first.Value.Dimension != second.Value.Dimension)
        {
            return false;
        }

        return Math.Abs(firstQuantity * first.Value.Scale - secondQuantity * second.Value.Scale) < 0.0001m;
    }
}
