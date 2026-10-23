using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;
using Spiza.Api.Configuration;

namespace Spiza.Api.Sources;

/// <summary>Reads Eurospin Croatia's public daily ZIP containing store CSV price lists.</summary>
public sealed class EurospinPublicPriceSource(HttpClient httpClient, SpizaOptions options) : IPublicPriceSource
{
    private const string IndexUrl = "https://www.eurospin.hr/cjenik/";
    private static readonly Regex ZipOption = new("<option\\b(?=[^>]*\\bvalue\\s*=\\s*['\"]([^'\"]+\\.zip(?:\\?[^'\"]*)?)['\"])[^>]*>", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public string Name => "eurospin";
    public bool Enabled => options.EurospinEnabled;

    public async Task<SourceFetchResult> FetchAsync(CancellationToken cancellationToken)
    {
        var today = options.Today;
        var requests = new PublicSourceRequestSession(httpClient, options, uri => IsHost(uri, "eurospin.hr"));
        var html = await requests.GetTextAsync(IndexUrl, cancellationToken);
        var archives = ZipOption.Matches(html)
            .Select(match => WebUtility.HtmlDecode(match.Groups[1].Value))
            .Select(value => Uri.TryCreate(new Uri(IndexUrl), value, out var uri) ? uri : null)
            .Where(uri => uri is not null && IsHost(uri, "eurospin.hr"))
            .Select(uri => (Uri: uri!, Date: ReadDate(Uri.UnescapeDataString(uri!.AbsolutePath))))
            .Where(value => value.Date is not null && value.Date.Value <= today)
            .GroupBy(value => value.Uri.AbsoluteUri, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToArray();

        var date = archives.Select(file => file.Date!.Value).DefaultIfEmpty(DateTime.MinValue).Max();
        if (date == DateTime.MinValue)
        {
            throw new InvalidOperationException("Eurospin's public price list page contains no dated ZIP links.");
        }

        var entries = PublicArchive.ReadSelectedEntries(await requests.GetBytesAsync(
            archives.First(file => file.Date == date).Uri.AbsoluteUri, cancellationToken), ".csv", ParseStore, options,
            options.SourceArchiveMaxExpandedBytes);
        var lists = new List<SourcePriceList>();
        var failed = 0;
        foreach (var entry in entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var products = PublicCsvParser.ParseProducts(PublicSourceRequestSession.Decode(entry.Content));
                if (products.Count == 0)
                {
                    failed++;
                    continue;
                }

                lists.Add(new SourcePriceList(entry.Store, date, products));
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                failed++;
            }
        }

        return new SourceFetchResult(lists, failed);
    }

    private static SourceStore ParseStore(string fileName)
    {
        fileName = Uri.UnescapeDataString(Path.GetFileName(fileName));
        var parts = fileName.Split('-', StringSplitOptions.TrimEntries);
        var dateIndex = Array.FindIndex(parts, value => DateTime.TryParseExact(value, "dd.MM.yyyy", CultureInfo.InvariantCulture, DateTimeStyles.None, out _));
        if (dateIndex < 4)
        {
            throw new FormatException("Eurospin CSV filename did not include store metadata.");
        }

        var hasId = int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out _);
        var id = hasId ? parts[1] : parts[1].Replace('_', ' ');
        var address = hasId ? parts[2] : parts[1];
        var city = hasId ? parts[3] : parts[2];
        var postal = hasId ? parts[4] : parts[3];
        address = CleanName(address);
        city = CleanName(city);
        var postalCode = postal.Length == 5 && postal.All(char.IsDigit) ? postal : null;
        return new SourceStore(id, $"Eurospin {city}", address, postalCode, city);
    }

    private static string CleanName(string value) => value.Replace('_', ' ').Trim();

    private static DateTime? ReadDate(string path)
    {
        var match = Regex.Match(path, @"(\d{2})\.(\d{2})\.(\d{4})", RegexOptions.CultureInvariant);
        return match.Success && DateTime.TryParseExact(match.Value, "dd.MM.yyyy", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
            ? date.Date
            : null;
    }

    private static bool IsHost(Uri uri, string domain) =>
        uri.Host.Equals(domain, StringComparison.OrdinalIgnoreCase) || uri.Host.EndsWith('.' + domain, StringComparison.OrdinalIgnoreCase);
}
