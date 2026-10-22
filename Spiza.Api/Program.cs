using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Globalization;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using ModelContextProtocol.AspNetCore;
using ModelContextProtocol.Server;
using Spiza.Api.Configuration;
using Spiza.Api.Data;
using Spiza.Api.Services;
using Spiza.Api.Sources;

if (args is ["--healthcheck"])
{
    var port = Environment.GetEnvironmentVariable("SPIZA_PORT") ?? "7492";
    using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(4) };
    try
    {
        using var response = await client.GetAsync($"http://127.0.0.1:{port}/health/ready");
        return response.IsSuccessStatusCode ? 0 : 1;
    }
    catch (HttpRequestException)
    {
        return 1;
    }
    catch (TaskCanceledException)
    {
        return 1;
    }
}

var builder = WebApplication.CreateBuilder(args);
var spiza = SpizaOptions.FromConfiguration(builder.Configuration);
var configuredUrls = builder.Configuration["ASPNETCORE_URLS"] ?? $"http://0.0.0.0:{spiza.Port}";
builder.WebHost.UseUrls(configuredUrls);
builder.Logging.SetMinimumLevel(spiza.MinimumLogLevel);

var databasePath = Path.GetFullPath(spiza.DatabasePath);
Directory.CreateDirectory(Path.GetDirectoryName(databasePath)!);
var connectionString = new SqliteConnectionStringBuilder
{
    DataSource = databasePath,
    ForeignKeys = true,
    Pooling = true,
    DefaultTimeout = Math.Max(1, (spiza.SqliteBusyTimeoutMilliseconds + 999) / 1000)
}.ToString();

builder.Services.AddSingleton(spiza);
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddDbContextFactory<SpizaDbContext>(db => db.UseSqlite(connectionString));
builder.Services.AddSingleton<ReadinessService>();
builder.Services.AddSingleton<PriceQueryService>();
builder.Services.AddSingleton<RefreshService>();
builder.Services.AddHostedService<RefreshWorker>();
void ConfigureSourceClient(IServiceProvider services, HttpClient client)
{
    var settings = services.GetRequiredService<SpizaOptions>();
    client.Timeout = TimeSpan.FromSeconds(settings.SourceTimeoutSeconds);
    client.DefaultRequestHeaders.UserAgent.ParseAdd("SpizaApi/0.1");
    client.DefaultRequestHeaders.Accept.ParseAdd("text/html, text/csv, */*");
}

builder.Services.AddHttpClient<KonzumPublicPriceSource>(ConfigureSourceClient);
builder.Services.AddHttpClient<LidlPublicPriceSource>(ConfigureSourceClient);
builder.Services.AddHttpClient<TommyPublicPriceSource>(ConfigureSourceClient);
builder.Services.AddHttpClient<SparPublicPriceSource>(ConfigureSourceClient);
builder.Services.AddHttpClient<EurospinPublicPriceSource>(ConfigureSourceClient);
builder.Services.AddHttpClient<PlodinePublicPriceSource>(ConfigureSourceClient);
builder.Services.AddHttpClient<KauflandPublicPriceSource>(ConfigureSourceClient);
builder.Services.AddHttpClient<DmPublicPriceSource>(ConfigureSourceClient);
builder.Services.AddHttpClient<RibolaPublicPriceSource>(ConfigureSourceClient);
builder.Services.AddHttpClient<MetroPublicPriceSource>(ConfigureSourceClient);
builder.Services.AddTransient<IPublicPriceSource>(services => services.GetRequiredService<KonzumPublicPriceSource>());
builder.Services.AddTransient<IPublicPriceSource>(services => services.GetRequiredService<LidlPublicPriceSource>());
builder.Services.AddTransient<IPublicPriceSource>(services => services.GetRequiredService<TommyPublicPriceSource>());
builder.Services.AddTransient<IPublicPriceSource>(services => services.GetRequiredService<SparPublicPriceSource>());
builder.Services.AddTransient<IPublicPriceSource>(services => services.GetRequiredService<EurospinPublicPriceSource>());
builder.Services.AddTransient<IPublicPriceSource>(services => services.GetRequiredService<PlodinePublicPriceSource>());
builder.Services.AddTransient<IPublicPriceSource>(services => services.GetRequiredService<KauflandPublicPriceSource>());
builder.Services.AddTransient<IPublicPriceSource>(services => services.GetRequiredService<DmPublicPriceSource>());
builder.Services.AddTransient<IPublicPriceSource>(services => services.GetRequiredService<RibolaPublicPriceSource>());
builder.Services.AddTransient<IPublicPriceSource>(services => services.GetRequiredService<MetroPublicPriceSource>());

builder.Services.AddMcpServer()
    .WithHttpTransport(transport => transport.SessionMode = HttpServerSessionMode.Stateless)
    .WithToolsFromAssembly();

var app = builder.Build();
var apiKey = await ResolveApiKeyAsync(spiza, app.Logger);

await using (var scope = app.Services.CreateAsyncScope())
{
    var dbFactory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<SpizaDbContext>>();
    await using var db = await dbFactory.CreateDbContextAsync();
    await db.Database.MigrateAsync();
    await db.Database.ExecuteSqlRawAsync("PRAGMA journal_mode=WAL;");
}

app.Use(async (context, next) =>
{
    var path = context.Request.Path;
    var needsKey = path.StartsWithSegments("/api") || path.StartsWithSegments("/mcp");
    if (!needsKey)
    {
        await next();
        return;
    }

    var supplied = context.Request.Headers.Authorization.ToString();
    var token = supplied.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? supplied[7..].Trim() : string.Empty;
    var expectedBytes = Encoding.UTF8.GetBytes(apiKey);
    var suppliedBytes = Encoding.UTF8.GetBytes(token);
    if (suppliedBytes.Length == 0 || !CryptographicOperations.FixedTimeEquals(expectedBytes, suppliedBytes))
    {
        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        context.Response.ContentType = "application/problem+json";
        await context.Response.WriteAsync(JsonSerializer.Serialize(new
        {
            type = "about:blank",
            title = "Unauthorized",
            status = StatusCodes.Status401Unauthorized,
            detail = "Send the configured API key using the Bearer authorization scheme."
        }));
        return;
    }

    await next();
});

app.MapGet("/health/live", () => Results.Ok(new { status = "ok", service = "spiza-api" }));
app.MapGet("/health/ready", async (ReadinessService readiness, CancellationToken cancellationToken) =>
{
    try
    {
        var state = await readiness.CheckAsync(cancellationToken);
        if (!state.DatabaseAvailable)
        {
            return Results.Problem(statusCode: StatusCodes.Status503ServiceUnavailable, title: "Database unavailable");
        }

        if (!state.HasPriceData)
        {
            return Results.Problem(statusCode: StatusCodes.Status503ServiceUnavailable,
                title: "Price data unavailable", detail: "No price observations have been imported yet.");
        }

        return Results.Ok(new { status = "ready", database = "ok", priceData = "available" });
    }
    catch (Exception exception)
    {
        app.Logger.LogWarning(exception, "Database readiness check failed.");
        return Results.Problem(statusCode: StatusCodes.Status503ServiceUnavailable, title: "Database unavailable");
    }
});

var api = app.MapGroup("/api/v1");
api.MapGet("/products", async (string? q, string? barcode, string? retailer, int? limit,
    PriceQueryService queries, CancellationToken cancellationToken) =>
{
    if (string.IsNullOrWhiteSpace(q) && string.IsNullOrWhiteSpace(barcode))
    {
        return Results.BadRequest(new { status = "invalid_request", message = "Provide q or barcode." });
    }

    var products = await queries.SearchProductsAsync(q, barcode, retailer, limit, cancellationToken);
    var status = products.Count == 0 ? "unknown_product" :
        products.Any(product => product.Status == "incomplete_data") ? "incomplete_data" : "ok";
    return Results.Ok(new { status, data = products });
});

api.MapGet("/prices/current", async (string? barcode, string? q, string? city, string[]? fallbackAreas, string? storeId, string? retailer,
    PriceQueryService queries, CancellationToken cancellationToken) =>
{
    if (string.IsNullOrWhiteSpace(barcode) && string.IsNullOrWhiteSpace(q))
    {
        return Results.BadRequest(new { status = "invalid_request", message = "Provide barcode or q." });
    }

    return Results.Ok(await queries.GetCurrentPricesAsync(barcode, q, city, fallbackAreas, storeId, retailer, cancellationToken));
});

api.MapPost("/prices/batch", async (BatchPriceRequest request, SpizaOptions settings,
    PriceQueryService queries, CancellationToken cancellationToken) =>
{
    if (request.Barcodes is null || request.Barcodes.Count == 0)
    {
        return Results.BadRequest(new { status = "invalid_request", message = "Provide at least one barcode." });
    }

    if (request.Barcodes.Count > settings.BatchMax)
    {
        return Results.BadRequest(new { status = "invalid_request", message = $"Batch limit is {settings.BatchMax} barcodes." });
    }

    return Results.Ok(new
    {
        status = "ok",
        data = await queries.GetBatchPricesAsync(request.Barcodes, request.City, request.FallbackAreas, cancellationToken)
    });
});

api.MapGet("/prices/history", async (string barcode, string? storeId, string? retailer, DateTime? from, DateTime? to,
    PriceQueryService queries, CancellationToken cancellationToken) =>
{
    if (from is not null && to is not null && from.Value.Date > to.Value.Date)
    {
        return Results.BadRequest(new { status = "invalid_request", message = "from must be on or before to." });
    }

    return Results.Ok(await queries.GetPriceHistoryAsync(barcode, storeId, retailer, from, to, cancellationToken));
});

api.MapGet("/coverage", async (PriceQueryService queries, CancellationToken cancellationToken) =>
    Results.Ok(await queries.GetCoverageAsync(cancellationToken)));

api.MapGet("/export/prices", async (HttpContext context, string? format, string? city, string[]? fallbackAreas, string? retailer,
    PriceQueryService queries, SpizaOptions settings, CancellationToken cancellationToken) =>
{
    var prices = await queries.GetCurrentPricesForExportAsync(city, retailer, fallbackAreas, cancellationToken);
    if (settings.ExportMaxRows > 0)
    {
        prices = prices.Take(settings.ExportMaxRows).ToArray();
    }

    if (string.Equals(format, "csv", StringComparison.OrdinalIgnoreCase))
    {
        context.Response.ContentType = "text/csv; charset=utf-8";
        context.Response.Headers.ContentDisposition = "attachment; filename=spiza-prices.csv";
        await context.Response.WriteAsync("barcode,product,brand,retailer,storeId,store,city,priceDate,regularPrice,promotionalPrice,effectivePrice,unitPrice,unit\r\n", cancellationToken);
        foreach (var price in prices)
        {
            var fields = new[]
            {
                price.Product.Barcode,
                price.Product.Name,
                price.Product.Brand,
                price.Store.Retailer,
                price.Store.StoreId,
                price.Store.Name,
                price.Store.City,
                price.PriceDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                FormatDecimal(price.RegularPrice),
                FormatDecimal(price.PromotionalPrice),
                FormatDecimal(price.EffectivePrice),
                FormatDecimal(price.UnitPrice),
                price.UnitPriceUnit
            };
            await context.Response.WriteAsync(string.Join(',', fields.Select(CsvField)) + "\r\n", cancellationToken);
        }

        return Results.Empty;
    }

    if (string.IsNullOrWhiteSpace(format) || string.Equals(format, "json", StringComparison.OrdinalIgnoreCase))
    {
        return Results.Json(new { status = "ok", count = prices.Count, data = prices });
    }

    return Results.BadRequest(new { status = "invalid_request", message = "format must be json or csv." });
});

api.MapPost("/admin/refresh", (RefreshService refresh) =>
{
    refresh.StartBackgroundRefresh();
    return Results.Accepted("/api/v1/coverage", new { status = "accepted", message = "Source refresh queued." });
});

app.MapMcp("/mcp");

app.Run();
return 0;

static async Task<string> ResolveApiKeyAsync(SpizaOptions options, ILogger logger)
{
    if (!string.IsNullOrWhiteSpace(options.ApiKey))
    {
        return options.ApiKey;
    }

    var databasePath = Path.GetFullPath(options.DatabasePath);
    var keyPath = Path.Combine(Path.GetDirectoryName(databasePath)!, ".spiza-api-key");
    if (File.Exists(keyPath))
    {
        var existingKey = (await File.ReadAllTextAsync(keyPath)).Trim();
        if (!string.IsNullOrWhiteSpace(existingKey))
        {
            return existingKey;
        }
    }

    var key = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
        .TrimEnd('=')
        .Replace('+', '-')
        .Replace('/', '_');
    await File.WriteAllTextAsync(keyPath, key + Environment.NewLine, Encoding.UTF8);
    logger.LogWarning("SPIZA_API_KEY is unset. A random API key was saved to {ApiKeyPath}.", keyPath);
    return key;
}

static string CsvField(string? value)
{
    var text = value ?? string.Empty;
    return "\"" + text.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
}

static string FormatDecimal(decimal? value) => value?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;

public sealed record BatchPriceRequest(IReadOnlyList<string> Barcodes, string? City, IReadOnlyList<string>? FallbackAreas = null);
