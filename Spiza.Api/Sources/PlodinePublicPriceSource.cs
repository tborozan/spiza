using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;
using Spiza.Api.Configuration;

namespace Spiza.Api.Sources;

/// <summary>Reads Plodine's publicly linked daily ZIP with one CSV per store.</summary>
public sealed class PlodinePublicPriceSource(
    HttpClient httpClient,
    SpizaOptions options,
    ILogger<PlodinePublicPriceSource> logger) : IStreamingPublicPriceSource
{
    private const string BaseUrl = "https://www.plodine.hr";
    private const string IndexUrl = BaseUrl + "/info-o-cijenama";
    private static readonly Regex ZipLink = new("<a\\b(?=[^>]*\\bhref\\s*=\\s*['\"]([^'\"]+\\.zip(?:\\?[^'\"]*)?)['\"])[^>]*>", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex StoreName = new(
        @"^(?<type>SUPERMARKET|HIPERMARKET)_(?<address>.+?)_(?<postal>\d{5})_(?<city>.+)_(?<id>\d+)_\d+_\d+.*\.csv$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public string Name => "plodine";
    public bool Enabled => options.PlodineEnabled;

    public async Task<SourceFetchResult> FetchAsync(CancellationToken cancellationToken)
    {
        var lists = new List<SourcePriceList>();
        var failed = await FetchAndStreamAsync((list, _) =>
        {
            lists.Add(list);
            return Task.CompletedTask;
        }, cancellationToken);
        if (lists.Count == 0)
        {
            throw new InvalidOperationException("Plodine's public ZIP was found but no store CSV could be read.");
        }

        return new SourceFetchResult(lists, failed);
    }

    public async Task<int> FetchAndStreamAsync(
        Func<SourcePriceList, CancellationToken, Task> consume,
        CancellationToken cancellationToken)
    {
        var today = options.Today;
        var requests = new PublicSourceRequestSession(httpClient, options, uri => IsHost(uri, "plodine.hr"));
        var html = await requests.GetTextAsync(IndexUrl, cancellationToken);
        var archives = ZipLink.Matches(html)
            .Select(match => WebUtility.HtmlDecode(match.Groups[1].Value))
            .Select(value => Uri.TryCreate(new Uri(IndexUrl), value, out var uri) ? uri : null)
            .Where(uri => uri is not null && IsHost(uri, "plodine.hr"))
            .Select(uri => (Uri: uri!, Date: ReadDate(Uri.UnescapeDataString(uri!.AbsolutePath))))
            .Where(value => value.Date is not null && value.Date.Value <= today)
            .GroupBy(value => value.Uri.AbsoluteUri, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToArray();

        var date = archives.Select(file => file.Date!.Value).DefaultIfEmpty(DateTime.MinValue).Max();
        if (date == DateTime.MinValue)
        {
            throw new InvalidOperationException("Plodine's public price information page contains no dated ZIP links.");
        }

        var archive = await requests.GetBytesAsync(archives.First(file => file.Date == date).Uri.AbsoluteUri, cancellationToken);
        logger.LogInformation("Plodine archive downloaded: {CompressedBytes} bytes; expanded archive limit {ExpandedLimitBytes} bytes.",
            archive.Length, options.SourceArchiveMaxExpandedBytes);
        var failed = 0;
        var imported = 0;
        foreach (var entry in PublicArchive.ReadSelectedEntries(archive, ".csv", ParseStore, options,
                     options.SourceArchiveMaxExpandedBytes))
        {
            cancellationToken.ThrowIfCancellationRequested();
            SourcePriceList list;
            try
            {
                var products = PublicCsvParser.ParseProducts(PublicSourceRequestSession.Decode(entry.Content));
                if (products.Count == 0)
                {
                    failed++;
                    logger.LogWarning("Plodine price list {FileName} contained no parsed products.", entry.Name);
                    continue;
                }

                list = new SourcePriceList(entry.Store, date, products);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                failed++;
                logger.LogWarning(exception, "Could not parse Plodine price list {FileName}.", entry.Name);
                continue;
            }

            await consume(list, cancellationToken);
            imported++;
        }

        if (imported == 0 && failed > 0)
        {
            throw new InvalidOperationException("Plodine's public ZIP was found but no store CSV could be read.");
        }

        return failed;
    }

    private static SourceStore ParseStore(string fileName)
    {
        fileName = Uri.UnescapeDataString(Path.GetFileName(fileName));
        var match = StoreName.Match(fileName);
        if (!match.Success)
        {
            throw new FormatException("Plodine CSV filename did not include store metadata.");
        }

        var city = ToTitleCase(match.Groups["city"].Value.Replace('_', ' '));
        var address = ToTitleCase(match.Groups["address"].Value.Replace('_', ' '));
        return new SourceStore(match.Groups["id"].Value, $"Plodine {city}", address, match.Groups["postal"].Value, city);
    }

    private static DateTime? ReadDate(string path)
    {
        var match = Regex.Match(path, @"cjenici_(\d{2})_(\d{2})_(\d{4})_", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        return match.Success && DateTime.TryParseExact($"{match.Groups[1].Value}_{match.Groups[2].Value}_{match.Groups[3].Value}",
            "dd_MM_yyyy", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
            ? date.Date
            : null;
    }

    private static string ToTitleCase(string value) => CultureInfo.InvariantCulture.TextInfo.ToTitleCase(value.ToLowerInvariant());

    private static bool IsHost(Uri uri, string domain) =>
        uri.Host.Equals(domain, StringComparison.OrdinalIgnoreCase) || uri.Host.EndsWith('.' + domain, StringComparison.OrdinalIgnoreCase);
}
