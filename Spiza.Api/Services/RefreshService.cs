using System.Text.Json;
using System.Diagnostics;
using System.Threading.Channels;
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
    InitialRefreshState initialRefreshState,
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

            if (sourceNames is null || sourceNames.Contains("all"))
            {
                initialRefreshState.MarkCompleted();
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
        var sourceTimer = Stopwatch.StartNew();
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
            var fetchTimer = Stopwatch.StartNew();
            logger.LogDebug("Source refresh {Source} fetch and parse started.", source.Name);
            var streamingSource = source as IStreamingPublicPriceSource;
            var result = streamingSource is null ? await source.FetchAsync(cancellationToken) : null;
            var failedLists = result?.FailedLists ?? 0;
            var priceListCount = 0;
            var fetchedProductCount = 0;
            var storeIds = new HashSet<string>(StringComparer.Ordinal);
            var uniqueProductIds = new HashSet<string>(StringComparer.Ordinal);
            var insertedProductCount = 0;
            var updatedProductCount = 0;
            var insertedObservationCount = 0;
            var updatedObservationCount = 0;
            var unchangedObservationCount = 0;
            DateTime? latestPriceDate = null;
            var today = options.Today;
            var hasOutdatedLists = false;

            async IAsyncEnumerable<SourcePriceList> EnumeratePriceListsAsync()
            {
                if (result is not null)
                {
                    foreach (var fetchedList in result.PriceLists)
                    {
                        yield return fetchedList;
                    }

                    yield break;
                }

                var channel = Channel.CreateBounded<SourcePriceList>(new BoundedChannelOptions(1)
                {
                    SingleReader = true,
                    SingleWriter = true,
                    FullMode = BoundedChannelFullMode.Wait
                });
                using var streamCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                var fetchTask = streamingSource!.FetchAndStreamAsync(
                    async (list, token) => await channel.Writer.WriteAsync(list, token), streamCancellation.Token);
                var completionTask = CompleteStreamAsync();
                var consumedAllLists = false;
                try
                {
                    await foreach (var list in channel.Reader.ReadAllAsync(streamCancellation.Token))
                    {
                        yield return list;
                    }

                    consumedAllLists = true;
                }
                finally
                {
                    if (!consumedAllLists)
                    {
                        streamCancellation.Cancel();
                    }

                    try
                    {
                        await completionTask;
                    }
                    catch
                    {
                        // The channel surfaces producer failures through ReadAllAsync.
                    }
                }

                failedLists = await completionTask;

                async Task<int> CompleteStreamAsync()
                {
                    try
                    {
                        var failures = await fetchTask;
                        channel.Writer.TryComplete();
                        return failures;
                    }
                    catch (Exception exception)
                    {
                        channel.Writer.TryComplete(exception);
                        throw;
                    }
                }
            }

            await foreach (var list in EnumeratePriceListsAsync())
            {
                cancellationToken.ThrowIfCancellationRequested();
                priceListCount++;
                fetchedProductCount += list.Products.Count;
                latestPriceDate = latestPriceDate is null || list.PriceDate.Date > latestPriceDate.Value
                    ? list.PriceDate.Date
                    : latestPriceDate;
                hasOutdatedLists |= list.PriceDate.Date < today;
                var listTimer = Stopwatch.StartNew();
                await using var listDb = await dbFactory.CreateDbContextAsync(cancellationToken);
                var productPreparationTimer = Stopwatch.StartNew();
                var store = await UpsertStoreAsync(listDb, source.Name, list.Store, cancellationToken);
                storeIds.Add(list.Store.ExternalStoreId);
                var listProductIds = list.Products.Select(product => product.ExternalProductId).Distinct(StringComparer.Ordinal).ToArray();
                uniqueProductIds.UnionWith(listProductIds);
                var productMap = await LoadProductsAsync(listDb, source.Name, listProductIds, cancellationToken);
                var importFlags = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
                var productInsertions = 0;
                var productUpdates = 0;

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
                            Brand = Clean(imported.Brand),
                            Barcode = Clean(imported.Barcode),
                            NormalizedBarcode = barcode,
                            Category = Clean(imported.Category),
                            Quantity = imported.Quantity,
                            Unit = Clean(imported.Unit),
                            UpdatedAt = DateTime.UtcNow
                        };
                        productMap.Add(imported.ExternalProductId, product);
                        listDb.Products.Add(product);
                        productInsertions++;
                    }

                    var flags = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    if (product.Quantity is not null && imported.Quantity is not null &&
                        product.Unit is not null && imported.Unit is not null &&
                        !AreEquivalentQuantities(product.Quantity.Value, product.Unit, imported.Quantity.Value, imported.Unit) &&
                        !IsLegacyUnitCorrection(product, imported))
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

                    var name = imported.Name.Trim();
                    var brand = Clean(imported.Brand);
                    var importedBarcode = Clean(imported.Barcode);
                    var category = Clean(imported.Category);
                    var unit = Clean(imported.Unit);
                    if (product.Name != name || product.Brand != brand || product.Barcode != importedBarcode ||
                        product.NormalizedBarcode != barcode || product.Category != category ||
                        product.Quantity != imported.Quantity || product.Unit != unit)
                    {
                        product.Name = name;
                        product.Brand = brand;
                        product.Barcode = importedBarcode;
                        product.NormalizedBarcode = barcode;
                        product.Category = category;
                        product.Quantity = imported.Quantity;
                        product.Unit = unit;
                        product.UpdatedAt = DateTime.UtcNow;
                        if (product.Id != 0)
                        {
                            productUpdates++;
                        }
                    }
                }

                foreach (var duplicate in list.Products
                             .Where(product => NormalizeBarcode(product.Barcode) is not null)
                             .GroupBy(product => NormalizeBarcode(product.Barcode)!, StringComparer.Ordinal)
                             .Where(group => group.Select(product => product.ExternalProductId).Distinct(StringComparer.Ordinal).Count() > 1)
                             .SelectMany(group => group))
                {
                    importFlags[duplicate.ExternalProductId].Add("duplicate_product");
                }

                productPreparationTimer.Stop();
                var date = list.PriceDate.Date;
                var observationQueryTimer = Stopwatch.StartNew();
                var existingPrices = new Dictionary<string, PriceObservation>(StringComparer.Ordinal);
                if (store.Id != 0)
                {
                    var existingPriceRows = await listDb.PriceObservations
                        .Where(price => price.StoreId == store.Id && price.PriceDate == date)
                        .Include(price => price.Product)
                        .ToListAsync(cancellationToken);
                    existingPrices = existingPriceRows.ToDictionary(price => price.Product.ExternalProductId,
                        price => price, StringComparer.Ordinal);
                }

                observationQueryTimer.Stop();

                var observationInsertions = 0;
                var observationUpdates = 0;
                var observationUnchanged = 0;
                var retrievedAt = DateTime.UtcNow;
                var observationPreparationTimer = Stopwatch.StartNew();
                foreach (var imported in list.Products)
                {
                    var product = productMap[imported.ExternalProductId];
                    var flags = imported.QualityFlags.ToList();
                    flags.AddRange(importFlags[imported.ExternalProductId]);
                    if (list.Store.City is null)
                    {
                        flags.Add("store_location_missing");
                    }

                    var effective = ResolveEffectivePrice(imported.RegularPrice, imported.PromotionalPrice, list.PriceDate);
                    PriceObservation observation;
                    bool isNewObservation;
                    if (existingPrices.TryGetValue(imported.ExternalProductId, out var existingObservation))
                    {
                        observation = existingObservation;
                        isNewObservation = false;
                    }
                    else
                    {
                        observation = new PriceObservation
                        {
                            Product = product,
                            Store = store,
                            PriceDate = date,
                            RetrievedAt = DateTime.UtcNow
                        };
                        existingPrices.Add(imported.ExternalProductId, observation);
                        listDb.PriceObservations.Add(observation);
                        observationInsertions++;
                        isNewObservation = true;
                    }

                    var qualityFlagsJson = JsonSerializer.Serialize(flags.Distinct(StringComparer.OrdinalIgnoreCase));
                    var observationChanged = isNewObservation || observation.RegularPrice != imported.RegularPrice ||
                        observation.PromotionalPrice != imported.PromotionalPrice ||
                        observation.EffectivePrice != effective || observation.UnitPrice != imported.UnitPrice ||
                        observation.UnitPriceUnit != imported.UnitPriceUnit || observation.Currency != "EUR" ||
                        observation.QualityFlagsJson != qualityFlagsJson;
                    if (observationChanged)
                    {
                        observation.RegularPrice = imported.RegularPrice;
                        observation.PromotionalPrice = imported.PromotionalPrice;
                        observation.EffectivePrice = effective;
                        observation.UnitPrice = imported.UnitPrice;
                        observation.UnitPriceUnit = imported.UnitPriceUnit;
                        observation.Currency = "EUR";
                        observation.RetrievedAt = retrievedAt;
                        observation.QualityFlagsJson = qualityFlagsJson;
                        if (observation.Id != 0)
                        {
                            observationUpdates++;
                        }
                    }
                    else
                    {
                        observationUnchanged++;
                    }
                }

                observationPreparationTimer.Stop();
                var saveTimer = Stopwatch.StartNew();
                await listDb.SaveChangesAsync(cancellationToken);
                saveTimer.Stop();
                insertedProductCount += productInsertions;
                updatedProductCount += productUpdates;
                insertedObservationCount += observationInsertions;
                updatedObservationCount += observationUpdates;
                unchangedObservationCount += observationUnchanged;
                listTimer.Stop();
                logger.LogDebug(
                    "Source refresh {Source} list {StoreId} ({City}) date {PriceDate}: {ProductCount} products; product preparation {ProductPreparationMilliseconds} ms ({InsertedProducts} inserted, {UpdatedProducts} updated), observation lookup {ObservationQueryMilliseconds} ms, observation preparation {ObservationPreparationMilliseconds} ms, SaveChanges {SaveChangesMilliseconds} ms ({InsertedObservations} observations inserted, {UpdatedObservations} updated, {UnchangedObservations} unchanged), total {ElapsedMilliseconds} ms.",
                    source.Name, list.Store.ExternalStoreId, list.Store.City ?? "unknown", date, list.Products.Count,
                    productPreparationTimer.ElapsedMilliseconds, productInsertions, productUpdates,
                    observationQueryTimer.ElapsedMilliseconds, observationPreparationTimer.ElapsedMilliseconds,
                    saveTimer.ElapsedMilliseconds,
                    observationInsertions, observationUpdates, observationUnchanged, listTimer.ElapsedMilliseconds);
            }

            fetchTimer.Stop();
            hasOutdatedLists |= priceListCount == 0;
            logger.LogDebug("Source refresh {Source} fetch, parse, and import completed in {ElapsedMilliseconds} ms: {PriceListCount} lists, {ProductCount} products, {FailedLists} failed lists.",
                source.Name, fetchTimer.ElapsedMilliseconds, priceListCount, fetchedProductCount, failedLists);

            run.CompletedAt = DateTime.UtcNow;
            run.StoreCount = storeIds.Count;
            run.ProductCount = uniqueProductIds.Count;
            run.Status = failedLists > 0
                ? "partial"
                : hasOutdatedLists
                    ? "stale"
                    : "succeeded";
            if (failedLists > 0)
            {
                run.Error = $"{failedLists} public price list(s) could not be read.";
            }
            else if (run.Status == "stale")
            {
                run.Error = $"One or more public price lists are older than the current local date {today:yyyy-MM-dd}.";
            }

            await PruneHistoryAsync(db, cancellationToken);
            await db.SaveChangesAsync(cancellationToken);
            sourceTimer.Stop();
            logger.LogInformation("Source refresh {Source} finished with {Status} in {ElapsedMilliseconds} ms: {Stores} stores, {Products} unique products, {Failures} failed lists, latest price date {PriceDate}.",
                source.Name, run.Status, sourceTimer.ElapsedMilliseconds, storeIds.Count, uniqueProductIds.Count, failedLists, latestPriceDate);
            logger.LogDebug("Source refresh {Source} persistence totals: {InsertedProducts} products inserted, {UpdatedProducts} products updated, {InsertedObservations} observations inserted, {UpdatedObservations} observations updated, {UnchangedObservations} observations unchanged.",
                source.Name, insertedProductCount, updatedProductCount, insertedObservationCount, updatedObservationCount, unchangedObservationCount);
            return new RefreshSourceResult(source.Name, run.Status, storeIds.Count, uniqueProductIds.Count, failedLists, run.Error, latestPriceDate);
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
            store = new Store
            {
                Retailer = retailer,
                ExternalStoreId = imported.ExternalStoreId,
                Name = imported.Name,
                Address = imported.Address,
                PostalCode = imported.PostalCode,
                City = imported.City,
                UpdatedAt = DateTime.UtcNow
            };
            db.Stores.Add(store);
            return store;
        }

        if (store.Name == imported.Name && store.Address == imported.Address &&
            store.PostalCode == imported.PostalCode && store.City == imported.City)
        {
            return store;
        }

        store.Name = imported.Name;
        store.Address = imported.Address;
        store.PostalCode = imported.PostalCode;
        store.City = imported.City;
        store.UpdatedAt = DateTime.UtcNow;
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
        return ProductIdentity.NormalizeBarcode(barcode);
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

    private static bool IsLegacyUnitCorrection(Product existing, SourceProduct imported) =>
        existing.Quantity == imported.Quantity &&
        ((existing.Unit == "g" && imported.Unit == "kg") || (existing.Unit == "ml" && imported.Unit == "l"));
}
