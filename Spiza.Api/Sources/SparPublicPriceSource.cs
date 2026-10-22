using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Spiza.Api.Configuration;

namespace Spiza.Api.Sources;

/// <summary>Reads SPAR Croatia's public daily JSON index and linked store CSV files.</summary>
public sealed class SparPublicPriceSource(HttpClient httpClient, SpizaOptions options) : IPublicPriceSource
{
    private const string BaseUrl = "https://www.spar.hr";
    private static readonly Regex FileName = new(
        @"^(?<type>[A-Za-z]+)_(?<location>[A-Za-z0-9_\.]+)_(?<id>\d{4,5})_(?<name>[A-Za-z_]+)_",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly string[] KnownCities =
    [
        "varazdin", "valpovo", "sibenik", "zadar", "zagreb", "cakovec", "rijeka", "split", "kastav", "selce",
        "bibinje", "labin", "buje", "krizevci", "pozega", "jastrebarsko", "sesvetski_kraljevec", "krapinske_toplice",
        "novi_marof", "ivanic_grad", "vukovar", "marija_bistrica", "zapresic", "velika_gorica", "slavonski_brod",
        "osijek", "koprivnica", "bjelovar", "vinkovci", "dakovo", "orahovica", "pakrac", "suhopolje", "daruvar",
        "nasice", "pula", "opatija", "porec", "knin", "zlatar", "ivanec", "popovaca", "nin", "donja_stubica",
        "donji_stupnik", "pregrada", "cepin", "ozalj", "dugo_selo", "gospic", "sv._ivan_zelina", "kastel_sucurac"
    ];

    public string Name => "spar";
    public bool Enabled => options.SparEnabled;

    public async Task<SourceFetchResult> FetchAsync(CancellationToken cancellationToken)
    {
        var requests = new PublicSourceRequestSession(httpClient, options, uri =>
            uri.Host.Equals("spar.hr", StringComparison.OrdinalIgnoreCase) || uri.Host.EndsWith(".spar.hr", StringComparison.OrdinalIgnoreCase));
        var today = options.Today;
        Exception? lastError = null;
        JsonElement files = default;
        DateTime priceDate = today;
        for (var offset = 0; offset <= 2; offset++)
        {
            priceDate = today.AddDays(-offset);
            var indexUrl = $"{BaseUrl}/datoteke_cjenici/Cjenik{priceDate:yyyyMMdd}.json";
            try
            {
                using var document = JsonDocument.Parse(await requests.GetTextAsync(indexUrl, cancellationToken));
                if (document.RootElement.TryGetProperty("files", out var list) && list.ValueKind == JsonValueKind.Array && list.GetArrayLength() > 0)
                {
                    files = list.Clone();
                    lastError = null;
                    break;
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                lastError = exception;
            }
        }

        if (files.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidOperationException("SPAR has no readable public price list for the last three days.", lastError);
        }

        var lists = new List<SourcePriceList>();
        var failed = 0;
        foreach (var item in files.EnumerateArray())
        {
            cancellationToken.ThrowIfCancellationRequested();
            var path = GetString(item, "URL");
            if (string.IsNullOrWhiteSpace(path))
            {
                failed++;
                continue;
            }

            try
            {
                var url = Uri.TryCreate(path, UriKind.Absolute, out var absolute) && absolute.Scheme is "http" or "https"
                    ? absolute.AbsoluteUri
                    : new Uri(new Uri(BaseUrl), path).AbsoluteUri;
                var store = ParseStore(GetString(item, "name") ?? Path.GetFileName(new Uri(url).AbsolutePath));
                var products = PublicCsvParser.ParseProducts(await requests.GetTextAsync(url, cancellationToken));
                if (products.Count == 0)
                {
                    failed++;
                    continue;
                }

                lists.Add(new SourcePriceList(store, priceDate, products));
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                failed++;
            }
        }

        if (lists.Count == 0)
        {
            throw new InvalidOperationException("SPAR's public price list index was found but no store CSV could be read.");
        }

        return new SourceFetchResult(lists, failed);
    }

    private static string? GetString(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static SourceStore ParseStore(string fileName)
    {
        fileName = Uri.UnescapeDataString(Path.GetFileName(fileName));
        var match = FileName.Match(fileName);
        if (!match.Success)
        {
            throw new FormatException("SPAR CSV filename did not include store metadata.");
        }

        var location = match.Groups["location"].Value;
        var cityToken = KnownCities.OrderByDescending(value => value.Length)
            .FirstOrDefault(value => location.StartsWith(value + "_", StringComparison.OrdinalIgnoreCase));
        string city;
        string address;
        if (cityToken is null)
        {
            var pieces = location.Split('_', 2);
            city = pieces[0];
            address = pieces.Length > 1 ? pieces[1] : string.Empty;
        }
        else
        {
            city = cityToken;
            address = location[(cityToken.Length + 1)..];
        }

        city = ToTitleCase(city.Replace('_', ' '));
        address = ToTitleCase(address.Replace('_', ' '));
        var name = ToTitleCase(match.Groups["name"].Value.Replace('_', ' '));
        return new SourceStore(match.Groups["id"].Value, $"SPAR {name}", address, null, city);
    }

    private static string ToTitleCase(string value) => CultureInfo.InvariantCulture.TextInfo.ToTitleCase(value.ToLowerInvariant());
}
