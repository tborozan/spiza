using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;
using Spiza.Api.Configuration;

namespace Spiza.Api.Sources;

/// <summary>Reads the public per-store CSV price lists linked from Lidl Croatia's price list page.</summary>
public sealed class LidlPublicPriceSource(HttpClient httpClient, SpizaOptions options) : IPublicPriceSource
{
    private const string IndexUrl = "https://www.lidl.hr/c/cijene/s10073252";
    private static readonly Regex CsvLink = new("<a\\b(?=[^>]*\\bhref\\s*=\\s*['\"]([^'\"]+\\.csv(?:\\?[^'\"]*)?)['\"])[^>]*>", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex StoreName = new(
        @"^(?<type>Supermarket)\s+(?<id>\d+)_+(?<address>.*?)_+(?<postal>\d{5})_+(?<city>[A-ZŠĐČĆŽ_\s-]+?)_(?<branch>\d+)_+(?<date>\d{1,2}\.\d{1,2}\.\d{4})_",
        RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public string Name => "lidl";
    public bool Enabled => options.LidlEnabled;

    public async Task<SourceFetchResult> FetchAsync(CancellationToken cancellationToken)
    {
        var today = options.Today;
        var requests = new PublicSourceRequestSession(httpClient, options, uri => IsHost(uri, "lidl.hr"));
        var html = await requests.GetTextAsync(IndexUrl, cancellationToken);
        var files = CsvLink.Matches(html)
            .Select(match => WebUtility.HtmlDecode(match.Groups[1].Value))
            .Select(value => Uri.TryCreate(new Uri(IndexUrl), value, out var uri) ? uri : null)
            .Where(uri => uri is not null && IsHost(uri, "lidl.hr"))
            .Select(uri => (Uri: uri!, Date: ReadPriceDate(Uri.UnescapeDataString(uri!.AbsolutePath))))
            .Where(value => value.Date is not null && value.Date.Value <= today)
            .GroupBy(value => value.Uri.AbsoluteUri, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToArray();

        var currentDate = files.Select(file => file.Date!.Value).DefaultIfEmpty(DateTime.MinValue).Max();
        if (currentDate == DateTime.MinValue)
        {
            throw new InvalidOperationException("Lidl's public price list page contains no dated CSV links.");
        }

        var candidates = new List<(Uri Uri, SourceStore Store)>();
        var failed = 0;
        foreach (var file in files.Where(file => file.Date == currentDate))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                candidates.Add((file.Uri, ParseStore(file.Uri)));
            }
            catch
            {
                failed++;
            }
        }

        var selectedFiles = SourceAreaSelection.Select(candidates, file => file.Store, options);
        var lists = new List<SourcePriceList>();
        foreach (var file in selectedFiles)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var csv = await requests.GetTextAsync(file.Uri.AbsoluteUri, cancellationToken);
                var products = PublicCsvParser.ParseProducts(csv);
                if (products.Count == 0)
                {
                    failed++;
                    continue;
                }

                lists.Add(new SourcePriceList(file.Store, currentDate, products));
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

        if (lists.Count == 0 && selectedFiles.Count > 0)
        {
            throw new InvalidOperationException("Lidl's latest public price lists could not be read.");
        }

        return new SourceFetchResult(lists, failed);
    }

    private static SourceStore ParseStore(Uri uri)
    {
        var filename = Uri.UnescapeDataString(Path.GetFileName(uri.AbsolutePath));
        var match = StoreName.Match(filename);
        if (!match.Success || !DateTime.TryParseExact(match.Groups["date"].Value, ["d.M.yyyy", "dd.MM.yyyy"],
                CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
        {
            throw new FormatException("Lidl CSV filename did not include valid store metadata.");
        }

        var id = match.Groups["id"].Value;
        var city = CleanName(match.Groups["city"].Value);
        var address = CleanName(match.Groups["address"].Value);
        return new SourceStore(id, $"Lidl {city}", address, match.Groups["postal"].Value, city);
    }

    private static DateTime? ReadPriceDate(string path)
    {
        var match = Regex.Match(path, @"_(\d{1,2})\.(\d{1,2})\.(\d{4})_", RegexOptions.CultureInvariant);
        if (!match.Success || !int.TryParse(match.Groups[1].Value, out var day) ||
            !int.TryParse(match.Groups[2].Value, out var month) || !int.TryParse(match.Groups[3].Value, out var year))
        {
            return null;
        }

        try
        {
            return new DateTime(year, month, day);
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
    }

    private static string CleanName(string value) => value.Replace('_', ' ').Trim();

    private static bool IsHost(Uri uri, string domain) =>
        uri.Host.Equals(domain, StringComparison.OrdinalIgnoreCase) || uri.Host.EndsWith('.' + domain, StringComparison.OrdinalIgnoreCase);
}
