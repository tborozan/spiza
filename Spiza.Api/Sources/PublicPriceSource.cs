using System.Globalization;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.WebUtilities;
using Spiza.Api.Configuration;

namespace Spiza.Api.Sources;

public sealed record SourceProduct(
    string ExternalProductId,
    string Name,
    string? Brand,
    string? Barcode,
    string? Category,
    decimal? Quantity,
    string? Unit,
    decimal? RegularPrice,
    decimal? PromotionalPrice,
    decimal? UnitPrice,
    string? UnitPriceUnit,
    IReadOnlyList<string> QualityFlags);

public sealed record SourceStore(string ExternalStoreId, string Name, string? Address, string? PostalCode, string? City);

public sealed record SourcePriceList(SourceStore Store, DateTime PriceDate, IReadOnlyList<SourceProduct> Products);

public sealed record SourceFetchResult(IReadOnlyList<SourcePriceList> PriceLists, int FailedLists);

public interface IPublicPriceSource
{
    string Name { get; }
    bool Enabled { get; }
    Task<SourceFetchResult> FetchAsync(CancellationToken cancellationToken);
}

/// <summary>Allows large public sources to import each parsed price list without retaining every list in memory.</summary>
public interface IStreamingPublicPriceSource : IPublicPriceSource
{
    Task<int> FetchAndStreamAsync(
        Func<SourcePriceList, CancellationToken, Task> consume,
        CancellationToken cancellationToken);
}

/// <summary>
/// Reads Konzum's publicly listed store CSV price lists. It deliberately uses only
/// the retailer's anonymous cjenici pages and CSV links; no account or API key is used.
/// </summary>
public sealed class KonzumPublicPriceSource(HttpClient httpClient, SpizaOptions options, ILogger<KonzumPublicPriceSource> logger) : IPublicPriceSource
{
    private const string BaseUrl = "https://www.konzum.hr";
    private const int PageLimit = 9;
    private static readonly Regex CsvAnchor = new("<a\\b(?=[^>]*\\bformat\\s*=\\s*['\"]csv['\"])([^>]*)>", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex HrefAttribute = new("\\bhref\\s*=\\s*['\"]([^'\"]+)['\"]", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex PostalAddress = new("^(.*)\\s+(\\d{5})\\s+(.+)$", RegexOptions.Compiled);

    public string Name => "konzum";
    public bool Enabled => options.KonzumEnabled;

    public async Task<SourceFetchResult> FetchAsync(CancellationToken cancellationToken)
    {
        var date = options.Today;
        var csvUrls = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var requests = 0;
        var pagesRead = 0;
        bool TryConsumeRequest()
        {
            if (requests >= options.SourceRequestBudget)
            {
                return false;
            }

            requests++;
            return true;
        }

        for (var page = 1; page <= PageLimit && requests < options.SourceRequestBudget; page++)
        {
            var pageUrl = $"{BaseUrl}/cjenici?date={date:yyyy-MM-dd}&page={page}";
            var html = await GetTextAsync(pageUrl, cancellationToken, TryConsumeRequest);
            pagesRead++;
            var found = ExtractCsvUrls(html);
            if (found.Count == 0)
            {
                break;
            }

            foreach (var url in found)
            {
                csvUrls.Add(url);
            }

            await RespectRequestDelayAsync(cancellationToken);
        }

        if (csvUrls.Count == 0)
        {
            throw new InvalidOperationException(pagesRead == 0
                ? "Konzum's public price list index could not be read."
                : "Konzum did not publish a price list index for today.");
        }

        var lists = new List<SourcePriceList>(csvUrls.Count);
        var failed = 0;
        foreach (var url in csvUrls)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (requests >= options.SourceRequestBudget)
            {
                failed++;
                continue;
            }

            try
            {
                var store = ParseStore(url);
                var csv = await GetTextAsync(url, cancellationToken, TryConsumeRequest);
                var products = ParseProducts(csv);
                if (products.Count == 0)
                {
                    failed++;
                    continue;
                }

                lists.Add(new SourcePriceList(store, date, products));
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                failed++;
                logger.LogWarning(exception, "Could not read one public Konzum price list: {Url}", url);
            }

            await RespectRequestDelayAsync(cancellationToken);
        }

        return new SourceFetchResult(lists, failed);
    }

    private async Task<string> GetTextAsync(string url, CancellationToken cancellationToken, Func<bool> tryConsumeRequest)
    {
        Exception? lastError = null;
        for (var attempt = 0; attempt <= options.SourceRetryCount; attempt++)
        {
            if (!tryConsumeRequest())
            {
                throw new InvalidOperationException("The configured source request budget has been reached.");
            }

            try
            {
                using var response = await httpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
                response.EnsureSuccessStatusCode();
                return await response.Content.ReadAsStringAsync(cancellationToken);
            }
            catch (HttpRequestException exception) when (attempt < options.SourceRetryCount &&
                (exception.StatusCode is null or HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests ||
                 (exception.StatusCode is not null && (int)exception.StatusCode.Value >= 500)))
            {
                lastError = exception;
                await DelayRetryAsync(attempt, cancellationToken);
            }
            catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested && attempt < options.SourceRetryCount)
            {
                lastError = exception;
                await DelayRetryAsync(attempt, cancellationToken);
            }
        }

        throw lastError ?? new HttpRequestException($"Could not retrieve public source URL: {url}");
    }

    private async Task DelayRetryAsync(int attempt, CancellationToken cancellationToken)
    {
        var delay = TimeSpan.FromSeconds(options.SourceRetryDelaySeconds * Math.Max(1, attempt + 1));
        await Task.Delay(delay, cancellationToken);
    }

    private Task RespectRequestDelayAsync(CancellationToken cancellationToken) =>
        options.SourceRequestDelayMilliseconds <= 0
            ? Task.CompletedTask
            : Task.Delay(options.SourceRequestDelayMilliseconds, cancellationToken);

    private static List<string> ExtractCsvUrls(string html)
    {
        var urls = new List<string>();
        foreach (Match anchor in CsvAnchor.Matches(html))
        {
            var href = HrefAttribute.Match(anchor.Groups[1].Value);
            if (!href.Success)
            {
                continue;
            }

            var decoded = WebUtility.HtmlDecode(href.Groups[1].Value);
            if (Uri.TryCreate(new Uri(BaseUrl), decoded, out var url) && url.Scheme == Uri.UriSchemeHttps &&
                (url.Host.Equals("konzum.hr", StringComparison.OrdinalIgnoreCase) || url.Host.EndsWith(".konzum.hr", StringComparison.OrdinalIgnoreCase)))
            {
                urls.Add(url.ToString());
            }
        }

        return urls;
    }

    private static SourceStore ParseStore(string url)
    {
        var query = QueryHelpers.ParseQuery(new Uri(url).Query);
        if (!query.TryGetValue("title", out var titleValue))
        {
            throw new FormatException("Price list URL has no store title metadata.");
        }

        var parts = Uri.UnescapeDataString(titleValue.ToString()).Replace('_', ' ').Split(',', StringSplitOptions.TrimEntries);
        if (parts.Length < 6)
        {
            throw new FormatException("Price list store title metadata is incomplete.");
        }

        var addressAndCity = parts.Length == 6 ? parts[1] : $"{parts[1]} {parts[2]}";
        var externalId = parts.Length == 6 ? parts[2] : parts[3];
        var address = PostalAddress.Match(addressAndCity);
        var street = address.Success ? address.Groups[1].Value.Trim() : addressAndCity;
        var postalCode = address.Success ? address.Groups[2].Value : null;
        var city = address.Success ? address.Groups[3].Value.Trim() : null;
        if (string.IsNullOrWhiteSpace(externalId))
        {
            throw new FormatException("Price list store ID is empty.");
        }

        return new SourceStore(externalId, city is null ? $"Konzum {externalId}" : $"Konzum {city}", street, postalCode, city);
    }

    private static List<SourceProduct> ParseProducts(string csv)
    {
        if (csv.Length > 0 && csv[0] == '\uFEFF')
        {
            csv = csv[1..];
        }

        var delimiter = DetectDelimiter(csv);
        var rows = ParseCsvRows(csv, delimiter);
        if (rows.Count < 2)
        {
            return [];
        }

        var headers = rows[0].Select((value, index) => (Key: value.Trim(), Index: index))
            .Where(pair => !string.IsNullOrWhiteSpace(pair.Key))
            .ToDictionary(pair => pair.Key, pair => pair.Index, StringComparer.OrdinalIgnoreCase);

        var products = new List<SourceProduct>(rows.Count - 1);
        foreach (var row in rows.Skip(1))
        {
            string? Read(string key) => headers.TryGetValue(key, out var index) && index < row.Count ? row[index].Trim() : null;
            var name = Read("NAZIV PROIZVODA");
            var externalId = Read("ŠIFRA PROIZVODA");
            if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(externalId))
            {
                continue;
            }

            var barcode = Read("BARKOD");
            var regular = ParseMoney(Read("MALOPRODAJNA CIJENA"));
            var promo = ParseMoney(Read("MPC ZA VRIJEME POSEBNOG OBLIKA PRODAJE"));
            var unitPrice = ParseMoney(Read("CIJENA ZA JEDINICU MJERE"));
            var measurement = ProductMeasurement.Normalize(Read("NETO KOLIČINA"), Read("JEDINICA MJERE"));
            var flags = new List<string>();
            if (string.IsNullOrWhiteSpace(barcode))
            {
                flags.Add("barcode_missing");
            }
            else if (!IsValidBarcode(barcode))
            {
                flags.Add("barcode_mismatch");
            }

            if (regular is null && promo is null)
            {
                flags.Add("price_missing");
            }

            flags.AddRange(measurement.QualityFlags);
            if (unitPrice is not null && measurement.UnitPriceUnit is null)
            {
                flags.Add("unit_inconsistent");
            }

            products.Add(new SourceProduct(
                externalId,
                name,
                Read("MARKA PROIZVODA"),
                barcode,
                Read("KATEGORIJA PROIZVODA"),
                measurement.Quantity,
                measurement.Unit,
                regular,
                promo,
                unitPrice,
                measurement.UnitPriceUnit,
                flags.Distinct(StringComparer.OrdinalIgnoreCase).ToArray()));
        }

        return products;
    }

    private static char DetectDelimiter(string csv)
    {
        var firstLine = csv.AsSpan(0, csv.IndexOfAny('\r', '\n') is var end && end >= 0 ? end : csv.Length);
        var commaCount = firstLine.Count(',');
        var semicolonCount = firstLine.Count(';');
        var tabCount = firstLine.Count('\t');
        return semicolonCount >= commaCount && semicolonCount >= tabCount ? ';' : tabCount > commaCount ? '\t' : ',';
    }

    private static List<List<string>> ParseCsvRows(string text, char delimiter)
    {
        var rows = new List<List<string>>();
        var row = new List<string>();
        var field = new StringBuilder();
        var quoted = false;

        for (var index = 0; index < text.Length; index++)
        {
            var character = text[index];
            if (character == '"')
            {
                if (quoted && index + 1 < text.Length && text[index + 1] == '"')
                {
                    field.Append('"');
                    index++;
                }
                else
                {
                    quoted = !quoted;
                }
            }
            else if (character == delimiter && !quoted)
            {
                row.Add(field.ToString());
                field.Clear();
            }
            else if ((character == '\n' || character == '\r') && !quoted)
            {
                if (character == '\r' && index + 1 < text.Length && text[index + 1] == '\n')
                {
                    index++;
                }

                row.Add(field.ToString());
                field.Clear();
                if (row.Any(value => value.Length > 0))
                {
                    rows.Add(row);
                }

                row = [];
            }
            else
            {
                field.Append(character);
            }
        }

        if (field.Length > 0 || row.Count > 0)
        {
            row.Add(field.ToString());
            rows.Add(row);
        }

        return rows;
    }

    private static decimal? ParseMoney(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var value = text.Trim().Replace("€", "", StringComparison.Ordinal).Replace("EUR", "", StringComparison.OrdinalIgnoreCase).Trim();
        var culture = value.Contains(',') ? CultureInfo.GetCultureInfo("hr-HR") : CultureInfo.InvariantCulture;
        if (decimal.TryParse(value, NumberStyles.Number, culture, out var local))
        {
            return local >= 0 ? local : null;
        }

        return decimal.TryParse(value, NumberStyles.Number, CultureInfo.InvariantCulture, out var invariant) && invariant >= 0
            ? invariant
            : null;
    }

    private static bool IsValidBarcode(string barcode)
    {
        return ProductIdentity.IsValidBarcode(barcode);
    }
}
