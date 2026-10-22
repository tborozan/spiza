using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Spiza.Api.Configuration;

namespace Spiza.Api.Sources;

/// <summary>Reads Tommy's anonymous store-price-table API and its public CSV downloads.</summary>
public sealed class TommyPublicPriceSource(HttpClient httpClient, SpizaOptions options) : IPublicPriceSource
{
    private const string ApiRoot = "https://spiza.tommy.hr/api/v2";
    private static readonly Regex PostalAddress = new("^(?<postal>\\d{5})\\s+(?<city>.+)$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public string Name => "tommy";
    public bool Enabled => options.TommyEnabled;

    public async Task<SourceFetchResult> FetchAsync(CancellationToken cancellationToken)
    {
        var requests = new PublicSourceRequestSession(httpClient, options, uri => uri.Host.Equals("spiza.tommy.hr", StringComparison.OrdinalIgnoreCase));
        var date = options.Today;
        JsonElement entries = default;
        DateTime selectedDate = date;
        Exception? lastError = null;
        for (var offset = 0; offset <= 2; offset++)
        {
            selectedDate = date.AddDays(-offset);
            var indexUrl = $"{ApiRoot}/shop/store-prices-tables?date={selectedDate:yyyy-MM-dd}&page=1&itemsPerPage=200&channelCode=general";
            try
            {
                using var document = JsonDocument.Parse(await requests.GetTextAsync(indexUrl, cancellationToken));
                if (document.RootElement.TryGetProperty("hydra:member", out var members) && members.ValueKind == JsonValueKind.Array && members.GetArrayLength() > 0)
                {
                    entries = members.Clone();
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

        if (entries.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidOperationException("Tommy has no readable public price list for the last three days.", lastError);
        }

        var lists = new List<SourcePriceList>();
        var failed = 0;
        foreach (var entry in entries.EnumerateArray())
        {
            cancellationToken.ThrowIfCancellationRequested();
            var fileName = GetString(entry, "fileName");
            var itemId = GetString(entry, "@id");
            if (string.IsNullOrWhiteSpace(fileName) || string.IsNullOrWhiteSpace(itemId))
            {
                failed++;
                continue;
            }

            try
            {
                var store = ParseStore(fileName);
                var csvUrl = ResolveDownloadUrl(itemId);
                var products = PublicCsvParser.ParseProducts(await requests.GetTextAsync(csvUrl, cancellationToken));
                if (products.Count == 0)
                {
                    failed++;
                    continue;
                }

                lists.Add(new SourcePriceList(store, selectedDate, products));
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
            throw new InvalidOperationException("Tommy's public price tables were listed but none could be read.");
        }

        return new SourceFetchResult(lists, failed);
    }

    private static string? GetString(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static string ResolveDownloadUrl(string id)
    {
        if (Uri.TryCreate(id, UriKind.Absolute, out var absolute) && absolute.Scheme is "http" or "https")
        {
            return absolute.AbsoluteUri;
        }

        return id.StartsWith("/api/v2", StringComparison.OrdinalIgnoreCase)
            ? "https://spiza.tommy.hr" + id
            : ApiRoot + (id.StartsWith('/') ? id : "/" + id);
    }

    private static SourceStore ParseStore(string fileName)
    {
        var parts = fileName.Split(',', StringSplitOptions.TrimEntries);
        if (parts.Length < 4)
        {
            throw new FormatException("Tommy price table filename does not contain store metadata.");
        }

        var location = PostalAddress.Match(parts[2]);
        var city = location.Success ? location.Groups["city"].Value.Trim() : parts[2].Trim();
        var postalCode = location.Success ? location.Groups["postal"].Value : null;
        var id = parts[3].Trim();
        if (id.Length == 0)
        {
            throw new FormatException("Tommy store ID is missing from the price table filename.");
        }

        var storeType = CultureInfo.InvariantCulture.TextInfo.ToTitleCase(parts[0].Trim().ToLowerInvariant());
        var address = CultureInfo.InvariantCulture.TextInfo.ToTitleCase(parts[1].Trim().ToLowerInvariant());
        return new SourceStore(id, $"Tommy {storeType} {address}", address, postalCode, city);
    }
}
