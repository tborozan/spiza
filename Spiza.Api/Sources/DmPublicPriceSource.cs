using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Spiza.Api.Configuration;

namespace Spiza.Api.Sources;

/// <summary>Reads dm Croatia's public nationwide XLSX price list.</summary>
public sealed class DmPublicPriceSource(HttpClient httpClient, SpizaOptions options) : IPublicPriceSource
{
    private const string ContentRoot = "https://content.services.dmtech.com/rootpage-dm-shop-hr-hr";
    private const string IndexUrl = ContentRoot + "/novo/promocije/nove-oznake-cijena-i-vazeci-cjenik-u-dm-u-2906632?mrclx=false";

    public string Name => "dm";
    public bool Enabled => options.DmEnabled;

    public async Task<SourceFetchResult> FetchAsync(CancellationToken cancellationToken)
    {
        var requests = new PublicSourceRequestSession(httpClient, options, uri =>
            uri.Host.Equals("content.services.dmtech.com", StringComparison.OrdinalIgnoreCase) ||
            uri.Host.Equals("dm.hr", StringComparison.OrdinalIgnoreCase) || uri.Host.EndsWith(".dm.hr", StringComparison.OrdinalIgnoreCase));
        using var index = System.Text.Json.JsonDocument.Parse(await requests.GetTextAsync(IndexUrl, cancellationToken));
        if (!index.RootElement.TryGetProperty("mainData", out var mainData) || mainData.ValueKind != System.Text.Json.JsonValueKind.Array)
        {
            throw new InvalidDataException("dm's public price list index has an unexpected format.");
        }

        var today = options.Today;
        var files = mainData.EnumerateArray()
            .Where(item => ReadString(item, "type") == "CMDownload" && item.TryGetProperty("data", out _))
            .Select(item => item.GetProperty("data"))
            .Select(item => (Title: ReadString(item, "headline"), Path: ReadString(item, "linkTarget")))
            .Where(item => item.Title is not null && item.Path is not null)
            .Select(item => (item.Title!, Uri: new Uri(new Uri(ContentRoot), item.Path!), Date: ReadDate(item.Title!)))
            .Where(item => item.Date is not null && item.Date.Value <= today)
            .OrderByDescending(item => item.Date)
            .ToArray();

        if (files.Length == 0)
        {
            throw new InvalidOperationException("dm's public page contains no dated Excel price list.");
        }

        var selected = files[0];
        var products = ParseProducts(await requests.GetBytesAsync(selected.Uri.AbsoluteUri, cancellationToken));
        if (products.Count == 0)
        {
            throw new InvalidOperationException("dm's public Excel price list contained no readable products.");
        }

        return new SourceFetchResult(
            [new SourcePriceList(new SourceStore("all", "dm (all stores)", null, null, null), selected.Date!.Value, products)], 0);
    }

    internal static List<SourceProduct> ParseProducts(byte[] workbookBytes)
    {
        using var stream = new MemoryStream(workbookBytes);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read);
        var totalBytes = archive.Entries.Sum(entry => entry.Length);
        if (archive.Entries.Count > 1000 || totalBytes > 100_000_000)
        {
            throw new InvalidDataException("dm's public workbook exceeds the configured safety limits.");
        }

        var sharedStrings = ReadSharedStrings(archive);
        var sheetPath = GetFirstWorksheetPath(archive);
        var sheetEntry = archive.GetEntry(sheetPath) ?? throw new InvalidDataException("dm workbook has no worksheet.");
        using var sheetStream = sheetEntry.Open();
        var sheet = XDocument.Load(sheetStream);
        XNamespace main = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
        var indexedRows = new List<Dictionary<int, string>>();
        var maxColumn = 0;
        foreach (var row in sheet.Descendants(main + "row"))
        {
            var values = new Dictionary<int, string>();
            foreach (var cell in row.Elements(main + "c"))
            {
                var reference = (string?)cell.Attribute("r");
                var column = reference is null ? values.Count : GetColumnIndex(reference);
                var value = cell.Element(main + "v")?.Value;
                if (cell.Attribute("t")?.Value == "inlineStr")
                {
                    value = string.Concat(cell.Descendants(main + "t").Select(text => text.Value));
                }
                else if (cell.Attribute("t")?.Value == "s" && int.TryParse(value, out var sharedIndex) && sharedIndex >= 0 && sharedIndex < sharedStrings.Count)
                {
                    value = sharedStrings[sharedIndex];
                }

                values[column] = value ?? string.Empty;
                maxColumn = Math.Max(maxColumn, column);
            }

            indexedRows.Add(values);
        }

        var rows = indexedRows.Select(values => Enumerable.Range(0, maxColumn + 1)
            .Select(index => values.GetValueOrDefault(index, string.Empty)).ToArray()).ToList();
        var headerIndex = rows.FindIndex(row => row.Any(value => NormalizeHeader(value) == "nazivsifra"));
        if (headerIndex < 0)
        {
            var sample = string.Join(" | ", rows.Take(3).SelectMany(row => row).Where(value => !string.IsNullOrWhiteSpace(value)));
            throw new InvalidDataException($"dm workbook has no 'Naziv + šifra' header row. First cells: {sample}");
        }

        var headers = rows[headerIndex].Select(NormalizeHeader).ToArray();
        var nameIndex = Array.FindIndex(headers, value => value == "nazivsifra");
        int Find(params string[] keys)
        {
            for (var index = 0; index < headers.Length; index++)
            {
                if (keys.Any(key => headers[index] == NormalizeHeader(key)))
                {
                    return index;
                }
            }

            return -1;
        }

        var brand = Find("Marka", "Marka proizvoda");
        var barcode = Find("Barkod");
        var category = Find("Kategorija proizvoda", "Kategorija");
        var quantity = Find("Neto količina", "Neto kolicina");
        var unit = Find("Jedinica mjere");
        var regular = Find("MPC", "Maloprodajna cijena");
        var unitPrice = Find("Cijena za jedinicu mjere", "Cijena po JM");
        var promo = Find("MPC za vrijeme posebnog oblika prodaje (rasprodaja proizvoda koji izlaze iz asortimana)",
            "MPC za vrijeme posebnog oblika prodaje");
        var standardRows = new List<string?[]>
        {
            new string?[] { "NAZIV PROIZVODA", "ŠIFRA PROIZVODA", "MARKA PROIZVODA", "BARKOD", "KATEGORIJA PROIZVODA", "NETO KOLIČINA", "JEDINICA MJERE", "MALOPRODAJNA CIJENA", "MPC ZA VRIJEME POSEBNOG OBLIKA PRODAJE", "CIJENA ZA JEDINICU MJERE" }
        };
        foreach (var row in rows.Skip(headerIndex + 1))
        {
            var productId = Cell(row, nameIndex + 1);
            var productName = Cell(row, nameIndex);
            if (string.IsNullOrWhiteSpace(productId) || string.IsNullOrWhiteSpace(productName))
            {
                continue;
            }

            standardRows.Add([productName, productId, Cell(row, brand), Cell(row, barcode), Cell(row, category),
                Cell(row, quantity), Cell(row, unit), Cell(row, regular), Cell(row, promo), Cell(row, unitPrice)]);
        }

        return PublicCsvParser.ParseProducts(ToCsv(standardRows));
    }

    private static List<string> ReadSharedStrings(ZipArchive archive)
    {
        var entry = archive.GetEntry("xl/sharedStrings.xml");
        if (entry is null)
        {
            return [];
        }

        using var stream = entry.Open();
        var document = XDocument.Load(stream);
        XNamespace ns = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
        return document.Root?.Elements(ns + "si")
            .Select(item => string.Concat(item.Descendants(ns + "t").Select(text => text.Value))).ToList() ?? [];
    }

    private static string GetFirstWorksheetPath(ZipArchive archive)
    {
        var workbookEntry = archive.GetEntry("xl/workbook.xml") ?? throw new InvalidDataException("dm workbook metadata is missing.");
        var relationsEntry = archive.GetEntry("xl/_rels/workbook.xml.rels") ?? throw new InvalidDataException("dm workbook relationships are missing.");
        using var workbookStream = workbookEntry.Open();
        using var relationsStream = relationsEntry.Open();
        var workbook = XDocument.Load(workbookStream);
        var relations = XDocument.Load(relationsStream);
        XNamespace main = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
        XNamespace documentRelationships = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
        XNamespace packageRelationships = "http://schemas.openxmlformats.org/package/2006/relationships";
        var relationshipId = (string?)workbook.Root?.Element(main + "sheets")?.Elements(main + "sheet").FirstOrDefault()?.Attribute(documentRelationships + "id");
        var target = relations.Root?.Elements(packageRelationships + "Relationship")
            .FirstOrDefault(item => (string?)item.Attribute("Id") == relationshipId)?.Attribute("Target")?.Value;
        if (string.IsNullOrWhiteSpace(target))
        {
            throw new InvalidDataException("dm workbook has no worksheet relationship.");
        }

        return target.StartsWith('/') ? target[1..] : "xl/" + target.TrimStart('/');
    }

    private static int GetColumnIndex(string cellReference)
    {
        var index = 0;
        foreach (var character in cellReference.TakeWhile(char.IsLetter))
        {
            index = index * 26 + char.ToUpperInvariant(character) - 'A' + 1;
        }

        return Math.Max(0, index - 1);
    }

    private static string? Cell(string[] row, int index) => index >= 0 && index < row.Length ? row[index].Trim() : null;

    private static string NormalizeHeader(string value)
    {
        var decomposed = value.Normalize(NormalizationForm.FormD);
        return new string(decomposed.Where(character => CharUnicodeInfo.GetUnicodeCategory(character) != UnicodeCategory.NonSpacingMark && char.IsLetterOrDigit(character))
            .Select(char.ToLowerInvariant).ToArray());
    }

    private static DateTime? ReadDate(string value)
    {
        var match = Regex.Match(value, @"(?<day>\d{1,2})\.(?<month>\d{1,2})\.(?<year>\d{4})");
        return match.Success && DateTime.TryParseExact($"{match.Groups["day"].Value}.{match.Groups["month"].Value}.{match.Groups["year"].Value}",
            "d.M.yyyy", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date) ? date.Date : null;
    }

    private static string? ReadString(System.Text.Json.JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == System.Text.Json.JsonValueKind.String ? value.GetString() : null;

    private static string ToCsv(IEnumerable<string?[]> rows) => string.Join('\n', rows.Select(row => string.Join(';', row.Select(value => $"\"{(value ?? string.Empty).Replace("\"", "\"\"", StringComparison.Ordinal)}\""))));
}
