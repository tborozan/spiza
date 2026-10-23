using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Server;
using Spiza.Api.Configuration;
using Spiza.Api.Services;

namespace Spiza.Api.Mcp;

[McpServerToolType]
public static class SpizaMcpTools
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [McpServerTool, Description("Search grocery products by barcode or product text.")]
    public static async Task<string> SearchProducts(
        PriceQueryService queries,
        string? query = null,
        string? barcode = null,
        string? retailer = null,
        CancellationToken cancellationToken = default)
    {
        var result = await queries.SearchProductsAsync(query, barcode, retailer, null, cancellationToken);
        return JsonSerializer.Serialize(result, JsonOptions);
    }

    [McpServerTool, Description("Find current grocery prices by exact barcode or product text, with optional city, ordered fallback areas, store ID, and retailer filters. For Split and Solin requests both cities are primary, fallback areas are applied per retailer, and national dm prices remain included. Check each row's qualityFlags and effectivePriceUsable; a valid EUR effective price remains usable when its only issue is unit_inconsistent.")]
    public static async Task<string> CurrentPrices(
        PriceQueryService queries,
        string? barcode = null,
        string? query = null,
        string? city = null,
        string[]? fallbackAreas = null,
        string? storeId = null,
        string? retailer = null,
        CancellationToken cancellationToken = default)
    {
        var result = await queries.GetCurrentPricesAsync(barcode, query, city, fallbackAreas, storeId, retailer, cancellationToken);
        return JsonSerializer.Serialize(result, JsonOptions);
    }

    [McpServerTool, Description("Look up current prices for a batch of exact barcodes; Split and Solin are both primary areas and fallback areas are applied per retailer. Check each price row's qualityFlags and effectivePriceUsable; a valid EUR effective price remains usable when its only issue is unit_inconsistent.")]
    public static async Task<string> BatchPrices(
        PriceQueryService queries,
        SpizaOptions options,
        string[] barcodes,
        string? city = null,
        string[]? fallbackAreas = null,
        CancellationToken cancellationToken = default)
    {
        if (barcodes.Length > options.BatchMax)
        {
            return JsonSerializer.Serialize(new { status = "invalid_request", message = $"Batch limit is {options.BatchMax} barcodes." }, JsonOptions);
        }

        var result = await queries.GetBatchPricesAsync(barcodes, city, fallbackAreas, cancellationToken);
        return JsonSerializer.Serialize(result, JsonOptions);
    }

    [McpServerTool, Description("Return price history for a product barcode, optionally filtered by city, ordered fallback areas, retailer, store, and date range. For Split and Solin requests both cities are primary; national dm observations remain included.")]
    public static async Task<string> PriceHistory(
        PriceQueryService queries,
        string barcode,
        string? storeId = null,
        string? retailer = null,
        DateTime? from = null,
        DateTime? to = null,
        string? city = null,
        string[]? fallbackAreas = null,
        CancellationToken cancellationToken = default)
    {
        var result = await queries.GetPriceHistoryAsync(barcode, storeId, retailer, from, to, city, fallbackAreas, cancellationToken);
        return JsonSerializer.Serialize(result, JsonOptions);
    }

    [McpServerTool, Description("Summarize data freshness and retailer and city coverage.")]
    public static async Task<string> Coverage(PriceQueryService queries, CancellationToken cancellationToken = default)
    {
        var result = await queries.GetCoverageAsync(cancellationToken);
        return JsonSerializer.Serialize(result, JsonOptions);
    }
}
