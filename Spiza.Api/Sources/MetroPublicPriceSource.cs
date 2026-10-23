using System.Net;
using System.Text.RegularExpressions;
using System.Globalization;
using Spiza.Api.Configuration;

namespace Spiza.Api.Sources;

/// <summary>Reads Metro Croatia's public per-store CSV price lists.</summary>
public sealed class MetroPublicPriceSource(HttpClient httpClient, SpizaOptions options) : IPublicPriceSource
{
    private const string BaseUrl = "https://metrocjenik.com.hr";
    private static readonly Regex CsvLink = new("<a\\b(?=[^>]*\\bhref\\s*=\\s*['\"](?<url>[^'\"]+\\.csv(?:\\?[^'\"]*)?)['\"])[^>]*>", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex FileMetadata = new("^(?<type>.+?)_METRO_(?<date>\\d{8})T\\d{4}_(?<id>[^_]+)_(?<address>[^,]+),(?<city>.+)\\.csv$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public string Name => "metro";
    public bool Enabled => options.MetroEnabled;

    public async Task<SourceFetchResult> FetchAsync(CancellationToken cancellationToken)
    {
        var today = options.Today;
        var requests = new PublicSourceRequestSession(httpClient, options, uri => uri.Host.Equals("metrocjenik.com.hr", StringComparison.OrdinalIgnoreCase));
        var html = await requests.GetTextAsync(BaseUrl, cancellationToken);
        var files = CsvLink.Matches(html)
            .Select(match => WebUtility.HtmlDecode(match.Groups["url"].Value))
            .Select(value => Uri.TryCreate(new Uri(BaseUrl), value, out var uri) ? uri : null)
            .Where(uri => uri is not null && uri.Host.Equals("metrocjenik.com.hr", StringComparison.OrdinalIgnoreCase))
            .Select(uri => (Uri: uri!, Date: ReadDate(uri!.AbsolutePath)))
            .Where(item => item.Date is not null && item.Date.Value <= today)
            .ToArray();
        var latestDate = files.Select(file => file.Date!.Value).DefaultIfEmpty(DateTime.MinValue).Max();
        if (latestDate == DateTime.MinValue)
        {
            throw new InvalidOperationException("Metro's public price index contains no dated CSV links.");
        }

        var candidates = new List<(Uri Uri, SourceStore Store)>();
        var failed = 0;
        foreach (var file in files.Where(file => file.Date == latestDate))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var filename = Uri.UnescapeDataString(Path.GetFileName(file.Uri.AbsolutePath));
                var match = FileMetadata.Match(filename);
                if (!match.Success)
                {
                    throw new FormatException("Metro CSV filename did not include store metadata.");
                }

                var city = ToTitleCase(CleanName(match.Groups["city"].Value));
                var storeId = match.Groups["id"].Value;
                var store = new SourceStore(storeId, $"Metro {city} {storeId}",
                    ToTitleCase(CleanName(match.Groups["address"].Value)), null, city);
                candidates.Add((file.Uri, store));
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
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
                var products = PublicCsvParser.ParseProducts(await requests.GetTextAsync(file.Uri.AbsoluteUri, cancellationToken));
                if (products.Count == 0)
                {
                    failed++;
                    continue;
                }

                lists.Add(new SourcePriceList(file.Store, latestDate, products));
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
            throw new InvalidOperationException("Metro's public index was found but no store CSV could be read.");
        }

        return new SourceFetchResult(lists, failed);
    }

    private static DateTime? ReadDate(string path)
    {
        var match = Regex.Match(Uri.UnescapeDataString(path), @"_METRO_(?<date>\d{8})T", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        return match.Success && DateTime.TryParseExact(match.Groups["date"].Value, "yyyyMMdd", CultureInfo.InvariantCulture,
            DateTimeStyles.None, out var date) ? date.Date : null;
    }

    private static string CleanName(string value) => value.Trim('_').Replace('_', ' ').Replace("  ", " ", StringComparison.Ordinal).Trim();

    private static string ToTitleCase(string value) => CultureInfo.InvariantCulture.TextInfo.ToTitleCase(value.ToLowerInvariant());
}
