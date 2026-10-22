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

        return products.Select(product => ToProductResult(product, duplicateKeys.Contains((product.Retailer, product.NormalizedBarcode)))).ToArray();
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
        CancellationToken cancellationToken)
    {
        var normalized = NormalizeBarcode(barcode);
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

        if (from is not null)
        {
            query = query.Where(price => price.PriceDate >= from.Value.Date);
        }

        if (to is not null)
        {
            query = query.Where(price => price.PriceDate <= to.Value.Date);
        }

        var rows = await query.OrderBy(price => price.PriceDate).ToListAsync(cancellationToken);
        var results = rows.Select(row => ToPriceResult(row)).ToArray();
        var status = results.Length == 0
            ? await HasLatestSourceErrorAsync(db, cancellationToken) ? "source_error" : "no_local_price"
            : results.Any(result => result.QualityFlags.Count > 0) ? "incomplete_data" : "ok";
        return new PriceLookupResult(status, normalized, results.Length == 0 ? "No price history matched the requested store and date range." : null, null, results);
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
            .Select(price => new { price.StoreId, price.Store.Retailer, price.Store.City, price.PriceDate })
            .GroupBy(price => new { price.Retailer, price.City })
            .Select(group => new
            {
                group.Key.Retailer,
                group.Key.City,
                StoreCount = group.Select(row => row.StoreId).Distinct().Count(),
                LatestPriceDate = group.Max(row => row.PriceDate)
            })
            .ToListAsync(cancellationToken);
        var latestPriceDates = areas.GroupBy(area => area.Retailer, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => (DateTime?)group.Max(area => area.LatestPriceDate), StringComparer.OrdinalIgnoreCase);
        var sourceCoverage = sourceGroups.Select(group =>
        {
            var lastAttempt = group.First();
            var lastSuccess = group.FirstOrDefault(run => run.Status is "succeeded" or "partial" or "stale");
            return new SourceCoverage(
                lastAttempt.Source,
                lastAttempt.Status,
                lastAttempt.StartedAt,
                lastSuccess?.CompletedAt,
                lastSuccess?.StoreCount ?? 0,
                lastSuccess?.ProductCount ?? 0,
                lastAttempt.Status == "partial" ? ParseFailureCount(lastAttempt.Error) : 0,
                lastAttempt.Error,
                latestPriceDates.GetValueOrDefault(lastAttempt.Source));
        }).ToList();
        foreach (var source in sources)
        {
            if (sourceCoverage.All(value => !value.Retailer.Equals(source.Name, StringComparison.OrdinalIgnoreCase)))
            {
                sourceCoverage.Add(new SourceCoverage(source.Name, source.Enabled ? "pending" : "disabled", null, null, 0, 0, 0, null,
                    latestPriceDates.GetValueOrDefault(source.Name)));
            }
        }

        return new CoverageResult(DateTime.UtcNow, productCount, storeCount, observationCount, sourceCoverage,
            areas.Where(area => area.City is not null)
                .Select(area => new AreaCoverage(area.Retailer, area.City!, area.StoreCount, area.LatestPriceDate)).ToArray());
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
        if (normalized is not null)
        {
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

        string? matchedArea = null;
        var priceRows = scopedPrices.ToList();
        if (storeId is null && !string.IsNullOrWhiteSpace(city))
        {
            var candidateAreas = new List<string> { city.Trim() };
            if (fallbackAreas is not null)
            {
                candidateAreas.AddRange(fallbackAreas.Where(value => !string.IsNullOrWhiteSpace(value)));
            }

            foreach (var area in candidateAreas.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                var matching = priceRows.Where(price => price.Store.ExternalStoreId.Equals("all", StringComparison.OrdinalIgnoreCase) ||
                    NormalizeLocation(price.Store.City) == NormalizeLocation(area)).ToList();
                if (matching.Count > 0)
                {
                    priceRows = matching;
                    matchedArea = area;
                    break;
                }
            }

            if (matchedArea is null)
            {
                priceRows = [];
            }
        }

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
            return new PriceLookupResult(status, NormalizeBarcode(barcode), message, matchedArea, []);
        }

        var finalStatus = results.Any(result => result.QualityFlags.Count > 0 || result.EffectivePrice is null) ? "incomplete_data" : "ok";
        return new PriceLookupResult(finalStatus, NormalizeBarcode(barcode), null, matchedArea, results);
    }

    private PriceResult ToPriceResult(PriceObservation price, bool duplicate = false)
    {
        var product = ToProductResult(price.Product, duplicate);
        var flags = ParseFlags(price.QualityFlagsJson).Concat(product.QualityFlags).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var observationDate = price.PriceDate.Date;
        var promoActive = price.PromotionalPrice is not null &&
            (price.PromotionStart is null || price.PromotionStart.Value.Date <= observationDate) &&
            (price.PromotionEnd is null || price.PromotionEnd.Value.Date >= observationDate);
        var effective = options.EffectivePriceRule == "regular"
            ? price.RegularPrice
            : promoActive ? price.PromotionalPrice : price.RegularPrice;
        var status = effective is null || flags.Length > 0 ? "incomplete_data" : "ok";

        return new PriceResult(product,
            new StoreResult(price.Store.Retailer, price.Store.ExternalStoreId, price.Store.Name, price.Store.Address, price.Store.PostalCode, price.Store.City),
            price.PriceDate, price.RegularPrice, promoActive ? price.PromotionalPrice : null, effective,
            price.UnitPrice, price.UnitPriceUnit, price.Currency, flags, status);
    }

    private static ProductResult ToProductResult(Product product, bool duplicate)
    {
        var flags = new List<string>();
        if (string.IsNullOrWhiteSpace(product.NormalizedBarcode))
        {
            flags.Add("barcode_missing");
        }
        else if (!IsValidBarcode(product.Barcode ?? product.NormalizedBarcode))
        {
            flags.Add("barcode_mismatch");
        }

        if (duplicate)
        {
            flags.Add("duplicate_product");
        }

        return new ProductResult(product.Id, product.Retailer, product.ExternalProductId, product.Name,
            product.Brand, product.Barcode, product.Category, product.Quantity, product.Unit,
            flags.Count > 0 ? "incomplete_data" : "ok", flags);
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
        if (string.IsNullOrWhiteSpace(barcode))
        {
            return null;
        }

        var digits = new string(barcode.Where(char.IsDigit).ToArray());
        return digits.Length == 0 ? null : digits;
    }

    private static string NormalizeLocation(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var normalized = value.Trim().Normalize(NormalizationForm.FormD);
        return new string(normalized.Where(character => CharUnicodeInfo.GetUnicodeCategory(character) != UnicodeCategory.NonSpacingMark).ToArray())
            .ToLowerInvariant();
    }

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

    private static bool IsValidBarcode(string barcode)
    {
        var digits = new string(barcode.Where(char.IsDigit).ToArray());
        if (digits.Length is not (8 or 12 or 13 or 14) || digits.Length != barcode.Length)
        {
            return false;
        }

        var sum = 0;
        var weight = 3;
        for (var index = digits.Length - 2; index >= 0; index--)
        {
            sum += (digits[index] - '0') * weight;
            weight = weight == 3 ? 1 : 3;
        }

        return (10 - sum % 10) % 10 == digits[^1] - '0';
    }
}
