using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Spiza.Api.Configuration;
using Spiza.Api.Data;
using Spiza.Api.Sources;

namespace Spiza.Api.Services;

public sealed record ProductResult(
    int Id,
    string Retailer,
    string ExternalProductId,
    string Name,
    string? Brand,
    string? Barcode,
    string? Category,
    decimal? Quantity,
    string? Unit,
    string Status,
    IReadOnlyList<string> QualityFlags);

public sealed record StoreResult(
    string Retailer,
    string StoreId,
    string Name,
    string? Address,
    string? PostalCode,
    string? City);

public sealed record PriceResult(
    ProductResult Product,
    StoreResult Store,
    DateTime PriceDate,
    decimal? RegularPrice,
    decimal? PromotionalPrice,
    decimal? EffectivePrice,
    bool EffectivePriceUsable,
    decimal? UnitPrice,
    string? UnitPriceUnit,
    string Currency,
    IReadOnlyList<string> QualityFlags,
    string Status);

public sealed record PriceLookupResult(
    string Status,
    string? Barcode,
    string? Message,
    string? MatchedArea,
    IReadOnlyList<PriceResult> Prices);

public sealed record BatchPriceItem(string Barcode, PriceLookupResult Result);

public sealed record SourceCoverage(
    string Retailer,
    string Status,
    DateTime? LastAttempt,
    DateTime? LastSuccess,
    int StoreCount,
    int ProductCount,
    int FailedLists,
    string? Error,
    DateTime? LatestPriceDate);

public sealed record AreaCoverage(string Retailer, string City, int StoreCount, DateTime? LatestPriceDate);

public sealed record CoverageResult(
    DateTime GeneratedAt,
    int ProductCount,
    int StoreCount,
    int PriceObservationCount,
    IReadOnlyList<SourceCoverage> Sources,
    IReadOnlyList<AreaCoverage> Areas);

public sealed class PriceQueryService(IDbContextFactory<SpizaDbContext> dbFactory, SpizaOptions options, IEnumerable<IPublicPriceSource> sources)
{
    private static readonly HashSet<string> ProductQualityFlagNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "barcode_missing", "barcode_mismatch", "duplicate_product", "unit_inconsistent"
    };

    public async Task<IReadOnlyList<ProductResult>> SearchProductsAsync(
        string? query,
        string? barcode,
        string? retailer,
        int? limit,
        CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var products = await FindProductsAsync(db, query, barcode, retailer, limit ?? options.SearchMaxResults, cancellationToken);
        var duplicateKeys = products
            .Where(product => product.NormalizedBarcode is not null)
            .GroupBy(product => (product.Retailer, product.NormalizedBarcode))
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .ToHashSet();
        var observations = products.Count == 0
            ? []
            : await LoadLatestPricesAsync(db, products.Select(product => product.Id).ToArray(), cancellationToken);
        var observedProductFlags = observations
            .GroupBy(price => price.ProductId)
            .ToDictionary(group => group.Key, group => group
                .SelectMany(price => ParseFlags(price.QualityFlagsJson))
                .Where(ProductQualityFlagNames.Contains)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray());

        return products.Select(product => ToProductResult(product,
            duplicateKeys.Contains((product.Retailer, product.NormalizedBarcode)),
            observedProductFlags.GetValueOrDefault(product.Id) ?? [])).ToArray();
    }

    public async Task<PriceLookupResult> GetCurrentPricesAsync(
        string? barcode,
        string? query,
        string? city,
        IReadOnlyList<string>? fallbackAreas,
        string? storeId,
        string? retailer,
        CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var products = await FindProductsAsync(db, query, barcode, retailer, options.SearchMaxResults, cancellationToken);
        if (products.Count == 0)
        {
            var error = await HasLatestSourceErrorAsync(db, cancellationToken);
            return new PriceLookupResult(error ? "source_error" : "unknown_product", NormalizeBarcode(barcode),
                error ? "The product could not be checked because the latest source refresh failed." : "No matching product was found.", null, []);
        }

        var prices = await LoadLatestPricesAsync(db, products.Select(product => product.Id).ToArray(), cancellationToken);
        return BuildCurrentResult(products, prices, barcode, city, fallbackAreas, storeId, retailer,
            await HasLatestSourceErrorAsync(db, cancellationToken));
    }

    public async Task<IReadOnlyList<BatchPriceItem>> GetBatchPricesAsync(
        IReadOnlyList<string> barcodes,
        string? city,
        IReadOnlyList<string>? fallbackAreas,
        CancellationToken cancellationToken)
    {
        var normalized = barcodes.Select(NormalizeBarcode).Where(value => value is not null).Cast<string>().Distinct(StringComparer.Ordinal).ToArray();
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var products = normalized.Length == 0
            ? []
            : await db.Products.Where(product => normalized.Contains(product.NormalizedBarcode!)).ToListAsync(cancellationToken);
        var productIds = products.Select(product => product.Id).ToArray();
        var prices = productIds.Length == 0 ? [] : await LoadLatestPricesAsync(db, productIds, cancellationToken);
        var sourceError = await HasLatestSourceErrorAsync(db, cancellationToken);

        return barcodes.Select(original =>
        {
            var code = NormalizeBarcode(original);
            var matched = code is null ? [] : products.Where(product => product.NormalizedBarcode == code).ToList();
            var result = matched.Count == 0
                ? new PriceLookupResult(sourceError ? "source_error" : "unknown_product", code,
                    sourceError ? "The product could not be checked because the latest source refresh failed." : "No matching product was found.", null, [])
                : BuildCurrentResult(matched, prices.Where(price => matched.Any(product => product.Id == price.ProductId)).ToList(),
                    code, city, fallbackAreas, null, null, sourceError);
            return new BatchPriceItem(original, result);
        }).ToArray();
    }

    public async Task<PriceLookupResult> GetPriceHistoryAsync(
        string barcode,
        string? storeId,
        string? retailer,
        DateTime? from,
        DateTime? to,
        string? city,
        IReadOnlyList<string>? fallbackAreas,
        CancellationToken cancellationToken)
    {
        var normalized = NormalizeBarcode(barcode);
        if (normalized is null)
        {
            return new PriceLookupResult("unknown_product", null, "No matching product was found.", null, []);
        }

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var products = await db.Products
            .Where(product => product.NormalizedBarcode == normalized && (retailer == null || product.Retailer == retailer))
            .ToListAsync(cancellationToken);
        if (products.Count == 0)
        {
            return new PriceLookupResult("unknown_product", normalized, "No matching product was found.", null, []);
        }

        var ids = products.Select(product => product.Id).ToArray();
        var query = db.PriceObservations.Include(price => price.Product).Include(price => price.Store)
            .Where(price => ids.Contains(price.ProductId));
        if (storeId is not null)
        {
            query = query.Where(price => price.Store.ExternalStoreId == storeId);
        }
        else if (!string.IsNullOrWhiteSpace(city))
        {
            var locationNames = GetCandidateLocationNames(city, fallbackAreas)
                .Select(NormalizeLocation)
                .Where(value => value.Length > 0)
                .ToHashSet(StringComparer.Ordinal);
            var stores = await db.Stores.AsNoTracking()
                .Where(store => retailer == null || store.Retailer == retailer)
                .Select(store => new { store.Id, store.City, store.ExternalStoreId })
                .ToListAsync(cancellationToken);
            var storeIds = stores.Where(store =>
                    store.ExternalStoreId.Equals("all", StringComparison.OrdinalIgnoreCase) ||
                    (store.City is not null && locationNames.Contains(NormalizeLocation(store.City))))
                .Select(store => store.Id).ToArray();
            query = query.Where(price => storeIds.Contains(price.StoreId));
        }

        if (from is not null)
        {
            query = query.Where(price => price.PriceDate >= from.Value.Date);
        }

        if (to is not null)
        {
            query = query.Where(price => price.PriceDate <= to.Value.Date);
        }

        var rows = await query.OrderBy(price => price.PriceDate).ToListAsync(cancellationToken);
        var selectedRows = SelectLocationRows(rows, city, fallbackAreas, storeId is not null);
        var duplicateKeys = products
            .Where(product => product.NormalizedBarcode is not null)
            .GroupBy(product => (product.Retailer, product.NormalizedBarcode))
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .ToHashSet();
        var results = selectedRows.Prices.Select(row => ToPriceResult(row,
            duplicateKeys.Contains((row.Product.Retailer, row.Product.NormalizedBarcode)))).ToArray();
        var status = results.Length == 0
            ? await HasLatestSourceErrorAsync(db, cancellationToken) ? "source_error" : "no_local_price"
            : results.Any(result => result.QualityFlags.Count > 0 || result.EffectivePrice is null) ? "incomplete_data" : "ok";
        return new PriceLookupResult(status, normalized, results.Length == 0 ? "No price history matched the requested store, location, and date range." : null,
            selectedRows.MatchedArea, results);
    }

    public async Task<CoverageResult> GetCoverageAsync(CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var runs = await db.SourceRuns.AsNoTracking().OrderByDescending(run => run.StartedAt).ToListAsync(cancellationToken);
        var sourceGroups = runs.GroupBy(run => run.Source, StringComparer.OrdinalIgnoreCase).ToArray();
        var storeCount = await db.Stores.CountAsync(cancellationToken);
        var productCount = await db.Products.CountAsync(cancellationToken);
        var observationCount = await db.PriceObservations.CountAsync(cancellationToken);
        var areas = await db.PriceObservations.AsNoTracking()
            .Select(price => new
            {
                price.StoreId,
                price.ProductId,
                price.Store.Retailer,
                City = price.Store.ExternalStoreId == "all" ? "National" : price.Store.City ?? "Unknown",
                price.PriceDate
            })
            .GroupBy(price => new { price.Retailer, price.City })
            .Select(group => new
            {
                group.Key.Retailer,
                group.Key.City,
                StoreCount = group.Select(row => row.StoreId).Distinct().Count(),
                LatestPriceDate = group.Max(row => row.PriceDate)
            })
            .ToListAsync(cancellationToken);
        var retailerMetrics = await db.PriceObservations.AsNoTracking()
            .GroupBy(price => price.Store.Retailer)
            .Select(group => new
            {
                Retailer = group.Key,
                StoreCount = group.Select(price => price.StoreId).Distinct().Count(),
                ProductCount = group.Select(price => price.ProductId).Distinct().Count(),
                LatestPriceDate = group.Max(price => price.PriceDate)
            })
            .ToListAsync(cancellationToken);
        var metricsByRetailer = retailerMetrics.ToDictionary(metric => metric.Retailer, StringComparer.OrdinalIgnoreCase);
        var sourceCoverage = sourceGroups.Select(group =>
        {
            var lastAttempt = group.First();
            var lastSuccess = group.FirstOrDefault(run => run.Status is "succeeded" or "stale");
            var metrics = metricsByRetailer.GetValueOrDefault(lastAttempt.Source);
            return new SourceCoverage(
                lastAttempt.Source,
                lastAttempt.Status,
                lastAttempt.StartedAt,
                lastSuccess?.CompletedAt,
                metrics?.StoreCount ?? 0,
                metrics?.ProductCount ?? 0,
                lastAttempt.Status == "partial" ? ParseFailureCount(lastAttempt.Error) : 0,
                lastAttempt.Error,
                metrics is null ? null : metrics.LatestPriceDate);
        }).ToList();
        foreach (var source in sources)
        {
            if (sourceCoverage.All(value => !value.Retailer.Equals(source.Name, StringComparison.OrdinalIgnoreCase)))
            {
                var metrics = metricsByRetailer.GetValueOrDefault(source.Name);
                sourceCoverage.Add(new SourceCoverage(source.Name, source.Enabled ? "pending" : "disabled", null, null,
                    metrics?.StoreCount ?? 0, metrics?.ProductCount ?? 0, 0, null,
                    metrics is null ? null : metrics.LatestPriceDate));
            }
        }

        return new CoverageResult(DateTime.UtcNow, productCount, storeCount, observationCount, sourceCoverage,
            areas.Select(area => new AreaCoverage(area.Retailer, area.City, area.StoreCount, area.LatestPriceDate)).ToArray());
    }

    public async Task<IReadOnlyList<PriceResult>> GetCurrentPricesForExportAsync(
        string? city,
        string? retailer,
        IReadOnlyList<string>? fallbackAreas,
        CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        IQueryable<Product> productQuery = db.Products.AsNoTracking()
            .Where(product => retailer == null || product.Retailer == retailer)
            .OrderBy(product => product.Name)
            .ThenBy(product => product.Retailer);
        if (options.ExportMaxRows > 0)
        {
            productQuery = productQuery.Take(options.ExportMaxRows);
        }

        var products = await productQuery.ToListAsync(cancellationToken);
        if (products.Count == 0)
        {
            return [];
        }

        var prices = await LoadLatestPricesAsync(db, products.Select(product => product.Id).ToArray(), cancellationToken);
        var result = BuildCurrentResult(products, prices, null, city, fallbackAreas, null, retailer, false);
        return options.ExportMaxRows > 0 ? result.Prices.Take(options.ExportMaxRows).ToArray() : result.Prices;
    }

    private async Task<List<Product>> FindProductsAsync(
        SpizaDbContext db,
        string? query,
        string? barcode,
        string? retailer,
        int limit,
        CancellationToken cancellationToken)
    {
        var normalized = NormalizeBarcode(barcode);
        IQueryable<Product> products = db.Products.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(barcode))
        {
            if (normalized is null)
            {
                return [];
            }

            products = products.Where(product => product.NormalizedBarcode == normalized);
        }
        else if (!string.IsNullOrWhiteSpace(query))
        {
            var term = query.Trim().ToLowerInvariant();
            products = products.Where(product => product.Name.ToLower().Contains(term) ||
                (product.Brand != null && product.Brand.ToLower().Contains(term)) ||
                (product.Barcode != null && product.Barcode.Contains(term)));
        }
        else
        {
            return [];
        }

        if (retailer is not null)
        {
            products = products.Where(product => product.Retailer == retailer);
        }

        return await products.OrderBy(product => product.Name).Take(Math.Clamp(limit, 1, options.SearchMaxResults)).ToListAsync(cancellationToken);
    }

    private async Task<List<PriceObservation>> LoadLatestPricesAsync(SpizaDbContext db, int[] productIds, CancellationToken cancellationToken)
    {
        var today = options.Today;
        var rows = new List<PriceObservation>();
        foreach (var chunk in productIds.Chunk(400))
        {
            var latestPerStore = db.PriceObservations.AsNoTracking()
                .Where(price => chunk.Contains(price.ProductId) && price.PriceDate <= today)
                .GroupBy(price => new { price.ProductId, price.StoreId })
                .Select(group => new
                {
                    group.Key.ProductId,
                    group.Key.StoreId,
                    PriceDate = group.Max(price => price.PriceDate)
                });
            var latestRows = from price in db.PriceObservations.AsNoTracking()
                                 .Include(price => price.Product)
                                 .Include(price => price.Store)
                             join latest in latestPerStore
                                 on new { price.ProductId, price.StoreId, price.PriceDate }
                                 equals new { latest.ProductId, latest.StoreId, latest.PriceDate }
                             select price;
            rows.AddRange(await latestRows.ToListAsync(cancellationToken));
        }

        return rows;
    }

    private PriceLookupResult BuildCurrentResult(
        IReadOnlyList<Product> products,
        IReadOnlyList<PriceObservation> allPrices,
        string? barcode,
        string? city,
        IReadOnlyList<string>? fallbackAreas,
        string? storeId,
        string? retailer,
        bool sourceError)
    {
        var scopedPrices = allPrices.AsEnumerable();
        if (storeId is not null)
        {
            scopedPrices = scopedPrices.Where(price => price.Store.ExternalStoreId.Equals(storeId, StringComparison.OrdinalIgnoreCase));
        }

        if (retailer is not null)
        {
            scopedPrices = scopedPrices.Where(price => price.Store.Retailer.Equals(retailer, StringComparison.OrdinalIgnoreCase));
        }

        var priceRows = scopedPrices.ToList();
        var locationSelection = SelectLocationRows(priceRows, city, fallbackAreas, storeId is not null);
        priceRows = locationSelection.Prices;

        var duplicateKeys = products
            .Where(product => product.NormalizedBarcode is not null)
            .GroupBy(product => (product.Retailer, product.NormalizedBarcode))
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .ToHashSet();
        var productMap = products.ToDictionary(product => product.Id);
        var results = priceRows
            .Where(price => productMap.ContainsKey(price.ProductId))
            .Select(price => ToPriceResult(price, duplicateKeys.Contains((price.Product.Retailer, price.Product.NormalizedBarcode))))
            .OrderBy(price => price.EffectivePrice)
            .ThenBy(price => price.Store.Retailer)
            .ToArray();

        if (results.Length == 0)
        {
            var hasAnyPrice = allPrices.Count > 0;
            var status = hasAnyPrice ? "no_local_price" : sourceError ? "source_error" : "incomplete_data";
            var message = status switch
            {
                "no_local_price" => "The product has prices, but none match the requested location or store.",
                "source_error" => "No current price is available because the latest retailer source refresh failed.",
                _ => "The product is known, but no price observation is available yet."
            };
            return new PriceLookupResult(status, NormalizeBarcode(barcode), message, locationSelection.MatchedArea, []);
        }

        var finalStatus = results.Any(result => result.QualityFlags.Count > 0 || result.EffectivePrice is null) ? "incomplete_data" : "ok";
        return new PriceLookupResult(finalStatus, NormalizeBarcode(barcode), null, locationSelection.MatchedArea, results);
    }

    private static (List<PriceObservation> Prices, string? MatchedArea) SelectLocationRows(
        IReadOnlyList<PriceObservation> rows,
        string? city,
        IReadOnlyList<string>? fallbackAreas,
        bool explicitStore)
    {
        if (explicitStore || string.IsNullOrWhiteSpace(city))
        {
            return (rows.ToList(), null);
        }

        var primaryAreas = GetPrimaryAreas(city).Select(NormalizeLocation).ToHashSet(StringComparer.Ordinal);
        var fallback = (fallbackAreas ?? [])
            .Where(area => !string.IsNullOrWhiteSpace(area))
            .Select(area => area.Trim())
            .Where(area => !primaryAreas.Contains(NormalizeLocation(area)))
            .DistinctBy(NormalizeLocation, StringComparer.Ordinal)
            .ToArray();
        var selected = new List<PriceObservation>();

        foreach (var retailerProductRows in rows.GroupBy(row => (row.Store.Retailer, row.ProductId)))
        {
            selected.AddRange(retailerProductRows.Where(row => row.Store.ExternalStoreId.Equals("all", StringComparison.OrdinalIgnoreCase)));
            var localRows = retailerProductRows.Where(row => !row.Store.ExternalStoreId.Equals("all", StringComparison.OrdinalIgnoreCase)).ToArray();
            var primaryRows = localRows.Where(row => row.Store.City is not null && primaryAreas.Contains(NormalizeLocation(row.Store.City))).ToArray();
            if (primaryRows.Length > 0)
            {
                selected.AddRange(primaryRows);
                continue;
            }

            foreach (var area in fallback)
            {
                var normalizedArea = NormalizeLocation(area);
                var fallbackRows = localRows.Where(row => row.Store.City is not null &&
                    NormalizeLocation(row.Store.City) == normalizedArea).ToArray();
                if (fallbackRows.Length == 0)
                {
                    continue;
                }

                selected.AddRange(fallbackRows);
                break;
            }
        }

        var matchedAreas = selected
            .Select(row => row.Store.ExternalStoreId.Equals("all", StringComparison.OrdinalIgnoreCase)
                ? "National"
                : row.Store.City)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .DistinctBy(NormalizeLocation, StringComparer.Ordinal)
            .ToArray();
        return (selected, matchedAreas.Length == 0 ? null : string.Join(", ", matchedAreas));
    }

    private static IReadOnlyList<string> GetCandidateLocationNames(string city, IReadOnlyList<string>? fallbackAreas) =>
        GetPrimaryAreas(city).Concat((fallbackAreas ?? []).Where(area => !string.IsNullOrWhiteSpace(area)).Select(area => area.Trim()))
            .DistinctBy(NormalizeLocation, StringComparer.Ordinal).ToArray();

    private static IReadOnlyList<string> GetPrimaryAreas(string city) => NormalizeLocation(city) is "split" or "solin"
        ? ["Split", "Solin"]
        : [city.Trim()];

    private PriceResult ToPriceResult(PriceObservation price, bool duplicate = false)
    {
        var sourceFlags = ParseFlags(price.QualityFlagsJson);
        var product = ToProductResult(price.Product, duplicate,
            sourceFlags.Where(ProductQualityFlagNames.Contains));
        var flags = sourceFlags.Concat(product.QualityFlags).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var observationDate = price.PriceDate.Date;
        var promoActive = price.PromotionalPrice is not null &&
            (price.PromotionStart is null || price.PromotionStart.Value.Date <= observationDate) &&
            (price.PromotionEnd is null || price.PromotionEnd.Value.Date >= observationDate);
        var effective = options.EffectivePriceRule == "regular"
            ? price.RegularPrice
            : promoActive ? price.PromotionalPrice : price.RegularPrice;
        var status = effective is null || flags.Length > 0 ? "incomplete_data" : "ok";
        var effectivePriceUsable = effective is not null && price.Currency.Equals("EUR", StringComparison.OrdinalIgnoreCase) &&
            flags.All(flag => flag.Equals("unit_inconsistent", StringComparison.OrdinalIgnoreCase));

        return new PriceResult(product,
            new StoreResult(price.Store.Retailer, price.Store.ExternalStoreId, price.Store.Name, price.Store.Address, price.Store.PostalCode, price.Store.City),
            price.PriceDate, price.RegularPrice, promoActive ? price.PromotionalPrice : null, effective, effectivePriceUsable,
            price.UnitPrice, price.UnitPriceUnit, price.Currency, flags, status);
    }

    private static ProductResult ToProductResult(Product product, bool duplicate, IEnumerable<string>? additionalFlags = null)
    {
        var flags = additionalFlags?.Where(ProductQualityFlagNames.Contains).ToList() ?? [];
        if (string.IsNullOrWhiteSpace(product.NormalizedBarcode))
        {
            flags.Add("barcode_missing");
        }
        else if (!ProductIdentity.IsValidBarcode(product.Barcode ?? product.NormalizedBarcode))
        {
            flags.Add("barcode_mismatch");
        }

        if (duplicate)
        {
            flags.Add("duplicate_product");
        }

        var distinctFlags = flags.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        return new ProductResult(product.Id, product.Retailer, product.ExternalProductId, product.Name,
            product.Brand, product.Barcode, product.Category, product.Quantity, product.Unit,
            distinctFlags.Length > 0 ? "incomplete_data" : "ok", distinctFlags);
    }

    private static async Task<bool> HasLatestSourceErrorAsync(SpizaDbContext db, CancellationToken cancellationToken)
    {
        var recent = await db.SourceRuns.AsNoTracking().OrderByDescending(run => run.StartedAt)
            .Take(100).ToListAsync(cancellationToken);
        var latest = recent.GroupBy(run => run.Source, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First()).ToList();
        return latest.Count > 0 && latest.All(run => run.Status is "failed" or "partial" or "cancelled");
    }

    private static int ParseFailureCount(string? error)
    {
        if (string.IsNullOrWhiteSpace(error))
        {
            return 0;
        }

        var number = new string(error.SkipWhile(character => !char.IsDigit(character)).TakeWhile(char.IsDigit).ToArray());
        return int.TryParse(number, NumberStyles.Integer, CultureInfo.InvariantCulture, out var count) ? count : 0;
    }

    private static string? NormalizeBarcode(string? barcode)
    {
        return ProductIdentity.NormalizeBarcode(barcode);
    }

    private static string NormalizeLocation(string? value)
        => SpizaOptions.NormalizeAreaName(value);

    private static IReadOnlyList<string> ParseFlags(string value)
    {
        try
        {
            return JsonSerializer.Deserialize<string[]>(value) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

}
