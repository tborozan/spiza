using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;
using System.Text.Json;
using Spiza.Api.Configuration;

namespace Spiza.Api.Sources;

/// <summary>Reads Kaufland Croatia's public, per-store CSV price lists.</summary>
public sealed class KauflandPublicPriceSource(
    HttpClient httpClient,
    SpizaOptions options,
    ILogger<KauflandPublicPriceSource> logger) : IPublicPriceSource
{
    private const string BaseUrl = "https://www.kaufland.hr";
    private const string IndexUrl = BaseUrl + "/akcije-novosti/popis-mpc.html";
    private static readonly Regex AssetList = new("<div\\b(?=[^>]*\\bdata-component\\s*=\\s*['\"]AssetList['\"])(?<attributes>[^>]*)>", RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.Singleline);
    private static readonly Regex DataProps = new("\\bdata-props\\s*=\\s*['\"](?<value>.*?)['\"]", RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.Singleline);
    private static readonly Regex StorePattern = new("^(?<type>Supermarket|Hipermarket)_(?<address>.+?)_(?<id>\\d{4,5})_(?=\\d{8}_|\\d{1,2}_\\d{1,2}_\\d{4}_)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly string[] Cities =
    [
        "Slavonski Brod", "Nova Gradiska", "Velika Gorica", "Dugo Selo", "Zagreb Blato", "Zagreb", "Karlovac", "Zapresic",
        "Cakovec", "Dakovo", "Koprivnica", "Sibenik", "Virovitica", "Bjelovar", "Varazdin", "Makarska", "Biograd", "Rovinj",
        "Osijek", "Sinj", "Sisak", "Zadar", "Pula", "Porec", "Split", "Rijeka", "Trogir", "Umag", "Vukovar", "Zabok",
        "Pozega", "Vodice", "Samobor", "Ivanec", "Kutina", "Vinkovci", "Cibaca", "Vukovar"
    ];

    public string Name => "kaufland";
    public bool Enabled => options.KauflandEnabled;

    public async Task<SourceFetchResult> FetchAsync(CancellationToken cancellationToken)
    {
        var today = options.Today;
        var requests = new PublicSourceRequestSession(httpClient, options, uri => IsHost(uri, "kaufland.hr"));
        var html = await requests.GetTextAsync(IndexUrl, cancellationToken);
        var element = AssetList.Match(html);
        var propsMatch = element.Success ? DataProps.Match(element.Groups["attributes"].Value) : Match.Empty;
        if (!propsMatch.Success)
        {
            throw new InvalidOperationException("Kaufland's public price list page did not expose its asset index.");
        }

        using var props = JsonDocument.Parse(WebUtility.HtmlDecode(propsMatch.Groups["value"].Value));
        if (!props.RootElement.TryGetProperty("settings", out var settings) ||
            !settings.TryGetProperty("dataUrlAssets", out var dataUrlElement) ||
            string.IsNullOrWhiteSpace(dataUrlElement.GetString()))
        {
            throw new InvalidOperationException("Kaufland's public asset index URL was missing.");
        }

        var assetUri = new Uri(new Uri(BaseUrl), dataUrlElement.GetString()!);
        using var assets = JsonDocument.Parse(await requests.GetTextAsync(assetUri.AbsoluteUri, cancellationToken));
        if (assets.RootElement.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException("Kaufland's public asset index was not a JSON array.");
        }

        var files = assets.RootElement.EnumerateArray()
            .Select(item => (Label: ReadString(item, "label"), Path: ReadString(item, "path")))
            .Where(item => item.Label is not null && item.Path is not null)
            .Select(item => (Label: item.Label!, Uri: new Uri(new Uri(BaseUrl), item.Path!), Date: ReadDate(item.Label!) ?? ReadDate(item.Path!)))
            .Where(item => item.Date is not null && item.Date.Value <= today)
            .ToArray();
        var latestDate = files.Select(item => item.Date!.Value).DefaultIfEmpty(DateTime.MinValue).Max();
        if (latestDate == DateTime.MinValue)
        {
            throw new InvalidOperationException("Kaufland's public asset index contains no dated CSV files.");
        }

        var lists = new List<SourcePriceList>();
        var failed = 0;
        foreach (var file in files.Where(item => item.Date == latestDate))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var store = ParseStore(file.Label);
                var products = PublicCsvParser.ParseProducts(await requests.GetTextAsync(file.Uri.AbsoluteUri, cancellationToken));
                if (products.Count == 0)
                {
                    failed++;
                    continue;
                }

                lists.Add(new SourcePriceList(store, latestDate, products));
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                failed++;
                logger.LogWarning(exception, "Could not read Kaufland price list {FileName}.", file.Label);
            }
        }

        if (lists.Count == 0)
        {
            throw new InvalidOperationException("Kaufland's public price index was found but no store CSV could be read.");
        }

        return new SourceFetchResult(lists, failed);
    }

    private static SourceStore ParseStore(string label)
    {
        var fileName = Uri.UnescapeDataString(Path.GetFileName(label));
        var match = StorePattern.Match(fileName);
        if (!match.Success)
        {
            throw new FormatException("Kaufland CSV filename did not include store metadata.");
        }

        var address = Regex.Replace(match.Groups["address"].Value.Replace('_', ' '), @"\s+", " ").Trim();
        var normalizedAddress = Normalize(address);
        var city = Cities.OrderByDescending(value => value.Length)
            .FirstOrDefault(value => normalizedAddress.EndsWith(Normalize(value), StringComparison.Ordinal));
        var street = city is null ? address : address[..^city.Length].Trim();
        return new SourceStore(match.Groups["id"].Value,
            city is null ? $"Kaufland {match.Groups["id"].Value}" : $"Kaufland {city}",
            street, null, city);
    }

    private static DateTime? ReadDate(string value)
    {
        var match = Regex.Match(Uri.UnescapeDataString(value), @"_(?<day>\d{1,2})_(?<month>\d{1,2})_(?<year>\d{4})_", RegexOptions.CultureInvariant);
        if (match.Success && DateTime.TryParseExact($"{match.Groups["day"].Value}_{match.Groups["month"].Value}_{match.Groups["year"].Value}",
                "d_M_yyyy", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
        {
            return date.Date;
        }

        match = Regex.Match(Uri.UnescapeDataString(value), @"_(?<day>\d{2})(?<month>\d{2})(?<year>\d{4})_", RegexOptions.CultureInvariant);
        return match.Success && DateTime.TryParseExact(match.Value.Trim('_'), "ddMMyyyy", CultureInfo.InvariantCulture,
            DateTimeStyles.None, out var compactDate) ? compactDate.Date : null;
    }

    private static string? ReadString(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static string Normalize(string value)
    {
        var decomposed = value.Normalize(System.Text.NormalizationForm.FormD);
        return new string(decomposed.Where(character => CharUnicodeInfo.GetUnicodeCategory(character) != UnicodeCategory.NonSpacingMark)
            .ToArray()).ToLowerInvariant();
    }

    private static bool IsHost(Uri uri, string domain) =>
        uri.Host.Equals(domain, StringComparison.OrdinalIgnoreCase) || uri.Host.EndsWith('.' + domain, StringComparison.OrdinalIgnoreCase);
}
