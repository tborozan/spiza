using System.Globalization;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Spiza.Api.Configuration;

namespace Spiza.Api.Sources;

/// <summary>Reads Ribola's publicly linked per-store XML price lists.</summary>
public sealed class RibolaPublicPriceSource(HttpClient httpClient, SpizaOptions options) : IPublicPriceSource
{
    private const string BaseUrl = "https://ribola.hr";
    private const string IndexUrl = BaseUrl + "/ribola-cjenici/";
    private static readonly Regex XmlLink = new("<a\\b(?=[^>]*\\bhref\\s*=\\s*['\"]([^'\"]+\\.xml(?:\\?[^'\"]*)?)['\"])[^>]*>", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly string[] Cities =
    [
        "Kaštel Kambelovac", "Kaštel Gomilica", "Kaštel Lukšić", "Kaštel Stari", "Kaštel Novi", "Kaštel Sućurac",
        "Okrug Gornji", "Seget Donji", "Dugi Rat", "Primošten", "Dubrovnik", "Makarska", "Orebić", "Nečujam",
        "Podstrana", "Šibenik", "Stobreč", "Trogir", "Ploče", "Brela", "Ražanj", "Split", "Sinj", "Solin", "Jelsa", "Trilj", "Zadar"
    ];

    public string Name => "ribola";
    public bool Enabled => options.RibolaEnabled;

    public async Task<SourceFetchResult> FetchAsync(CancellationToken cancellationToken)
    {
        var today = options.Today;
        var requests = new PublicSourceRequestSession(httpClient, options, uri => IsHost(uri, "ribola.hr"));
        var indexUrl = $"{IndexUrl}?date={today:dd.MM.yyyy}";
        var html = await requests.GetTextAsync(indexUrl, cancellationToken);
        var urls = XmlLink.Matches(html)
            .Select(match => WebUtility.HtmlDecode(match.Groups[1].Value))
            .Select(value => Uri.TryCreate(new Uri(IndexUrl), value, out var uri) ? uri : null)
            .Where(uri => uri is not null && IsHost(uri, "ribola.hr"))
            .Select(uri => uri!.AbsoluteUri)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var lists = new List<SourcePriceList>();
        var failed = 0;
        foreach (var url in urls)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var document = XDocument.Parse(await requests.GetTextAsync(url, cancellationToken));
                var storeElement = document.Descendants().FirstOrDefault(element => Normalize(element.Name.LocalName) == "prodajniobjekt")
                    ?? throw new FormatException("Ribola XML has no ProdajniObjekt element.");
                var storeId = ReadChild(storeElement, "Oznaka");
                var addressAndCity = ReadChild(storeElement, "Adresa");
                if (string.IsNullOrWhiteSpace(storeId) || string.IsNullOrWhiteSpace(addressAndCity))
                {
                    throw new FormatException("Ribola XML is missing the store ID or address.");
                }

                var (address, city) = SplitCity(addressAndCity);
                var standardRows = new List<string?[]>
                {
                    new string?[] { "NAZIV PROIZVODA", "ŠIFRA PROIZVODA", "MARKA PROIZVODA", "BARKOD", "KATEGORIJA PROIZVODA", "NETO KOLIČINA", "JEDINICA MJERE", "MALOPRODAJNA CIJENA", "MPC ZA VRIJEME POSEBNOG OBLIKA PRODAJE", "CIJENA ZA JEDINICU MJERE" }
                };
                foreach (var product in document.Descendants().Where(element => Normalize(element.Name.LocalName) == "proizvod"))
                {
                    var name = ReadChild(product, "NazivProizvoda");
                    var productId = ReadChild(product, "SifraProizvoda");
                    if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(productId))
                    {
                        continue;
                    }

                    standardRows.Add([
                        name, productId, ReadChild(product, "MarkaProizvoda"), ReadChild(product, "Barkod"),
                        ReadChild(product, "KategorijeProizvoda", "KategorijaProizvoda"), ReadChild(product, "NetoKolicina"),
                        ReadChild(product, "JedinicaMjere"), ReadChild(product, "MaloprodajnaCijena"),
                        ReadChild(product, "MaloprodajnaCijenaAkcija"), ReadChild(product, "CijenaZaJedinicuMjere")
                    ]);
                }

                var products = PublicCsvParser.ParseProducts(ToCsv(standardRows));
                if (products.Count == 0)
                {
                    failed++;
                    continue;
                }

                lists.Add(new SourcePriceList(new SourceStore(storeId, $"Ribola {city ?? storeId}", address, null, city),
                    today, products));
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
            throw new InvalidOperationException("Ribola's public index contained no readable store price lists.");
        }

        return new SourceFetchResult(lists, failed);
    }

    private static string? ReadChild(XElement parent, params string[] names)
    {
        var normalized = names.Select(Normalize).ToHashSet(StringComparer.Ordinal);
        return parent.Elements().FirstOrDefault(element => normalized.Contains(Normalize(element.Name.LocalName)))?.Value.Trim();
    }

    private static (string Address, string? City) SplitCity(string value)
    {
        var address = value.Trim();
        var normalized = Normalize(address);
        var city = Cities.OrderByDescending(candidate => candidate.Length)
            .FirstOrDefault(candidate => normalized.EndsWith(Normalize(candidate), StringComparison.Ordinal));
        return city is null ? (address, null) : (address[..^city.Length].Trim(), city);
    }

    private static string Normalize(string value)
    {
        var decomposed = value.Normalize(NormalizationForm.FormD);
        return new string(decomposed.Where(character => CharUnicodeInfo.GetUnicodeCategory(character) != UnicodeCategory.NonSpacingMark && char.IsLetterOrDigit(character))
            .Select(char.ToLowerInvariant).ToArray());
    }

    private static string ToCsv(IEnumerable<string?[]> rows) => string.Join('\n', rows.Select(row => string.Join(';', row.Select(value => $"\"{(value ?? string.Empty).Replace("\"", "\"\"", StringComparison.Ordinal)}\""))));

    private static bool IsHost(Uri uri, string domain) =>
        uri.Host.Equals(domain, StringComparison.OrdinalIgnoreCase) || uri.Host.EndsWith('.' + domain, StringComparison.OrdinalIgnoreCase);
}
