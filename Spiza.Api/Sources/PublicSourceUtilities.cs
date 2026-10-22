using System.Globalization;
using System.IO.Compression;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using Spiza.Api.Configuration;

namespace Spiza.Api.Sources;

internal sealed class PublicSourceRequestSession(HttpClient client, SpizaOptions options, Func<Uri, bool> allowedUri)
{
    private int requests;

    public async Task<string> GetTextAsync(string url, CancellationToken cancellationToken)
    {
        return Decode(await GetBytesAsync(url, cancellationToken));
    }

    public async Task<byte[]> GetBytesAsync(string url, CancellationToken cancellationToken)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps || !allowedUri(uri))
        {
            throw new InvalidOperationException("The public price source returned a URL outside its approved HTTPS host.");
        }

        Exception? lastError = null;
        for (var attempt = 0; attempt <= options.SourceRetryCount; attempt++)
        {
            if (requests >= options.SourceRequestBudget)
            {
                throw new InvalidOperationException("The configured source request budget has been reached.");
            }

            if (requests > 0 && options.SourceRequestDelayMilliseconds > 0)
            {
                await Task.Delay(options.SourceRequestDelayMilliseconds, cancellationToken);
            }

            requests++;
            try
            {
                using var response = await client.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
                var transient = response.StatusCode is HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests ||
                               (int)response.StatusCode >= 500;
                if (transient && attempt < options.SourceRetryCount)
                {
                    await DelayRetryAsync(attempt, cancellationToken);
                    continue;
                }

                response.EnsureSuccessStatusCode();
                return await response.Content.ReadAsByteArrayAsync(cancellationToken);
            }
            catch (HttpRequestException exception) when (attempt < options.SourceRetryCount && IsTransient(exception))
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
        var seconds = options.SourceRetryDelaySeconds * Math.Max(1, attempt + 1);
        if (seconds > 0)
        {
            await Task.Delay(TimeSpan.FromSeconds(seconds), cancellationToken);
        }
    }

    private static bool IsTransient(HttpRequestException exception) =>
        exception.StatusCode is null or HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests ||
        (exception.StatusCode is not null && (int)exception.StatusCode.Value >= 500);

    internal static string Decode(byte[] bytes)
    {
        if (bytes.AsSpan().StartsWith(Encoding.UTF8.Preamble))
        {
            return Encoding.UTF8.GetString(bytes.AsSpan(Encoding.UTF8.Preamble.Length));
        }

        try
        {
            return new UTF8Encoding(false, true).GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            return Encoding.GetEncoding(1250).GetString(bytes);
        }
    }
}

internal static class PublicCsvParser
{
    public static List<SourceProduct> ParseProducts(string csv)
    {
        if (csv.Length > 0 && csv[0] == '\uFEFF')
        {
            csv = csv[1..];
        }

        var delimiter = DetectDelimiter(csv);
        var rows = ParseRows(csv, delimiter);
        if (rows.Count < 2)
        {
            return [];
        }

        var headers = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < rows[0].Count; index++)
        {
            var key = NormalizeHeader(rows[0][index]);
            if (key.Length > 0 && !headers.ContainsKey(key))
            {
                headers.Add(key, index);
            }
        }

        int? Column(params string[] aliases)
        {
            foreach (var alias in aliases)
            {
                if (headers.TryGetValue(NormalizeHeader(alias), out var index))
                {
                    return index;
                }
            }

            return null;
        }

        var nameColumn = Column("NAZIV PROIZVODA", "NAZIV", "NAZIV ARTIKLA");
        var idColumn = Column("ŠIFRA PROIZVODA", "ŠIFRA", "ŠIFRA ARTIKLA", "ID PROIZVODA");
        var brandColumn = Column("MARKA PROIZVODA", "MARKA", "BRAND");
        var barcodeColumn = Column("BARKOD", "BARKOD ARTIKLA", "EAN");
        var categoryColumn = Column("KATEGORIJA PROIZVODA", "KATEGORIJA", "ROBNA STRUKTURA", "WG");
        var quantityColumn = Column("NETO KOLIČINA", "NETO KOLIČINA KG", "NETO KOLICINA", "NETO_KOLICINA", "KOLIČINA");
        var unitColumn = Column("JEDINICA MJERE", "JEDINICA MJERE KG", "JEDINICA MJERE", "JED_MJERE", "UNIT");
        var regularColumn = Column("MALOPRODAJNA CIJENA", "MALOPROD.CIJENA(EUR)", "MPC (EUR)", "MPC", "REGULAR PRICE");
        var promoColumn = Column("MPC ZA VRIJEME POSEBNOG OBLIKA PRODAJE", "MPC POSEB.OBLIK PROD", "MPC POSEBNA PRODAJA", "POSEBNA_PRODAJA", "PROMOTIONAL PRICE");
        var unitPriceColumn = Column("CIJENA ZA JEDINICU MJERE", "CIJENA ZA JEDINICU MJERE (EUR)", "CIJENA PO JM", "CIJENA JED.MJ.(EUR)", "CIJENA_PO_MJERI", "UNIT PRICE");

        var products = new List<SourceProduct>(rows.Count - 1);
        foreach (var row in rows.Skip(1))
        {
            string? Value(int? column) => column is not null && column < row.Count ? WebUtility.HtmlDecode(row[column.Value]).Trim() : null;
            var name = Value(nameColumn);
            var externalId = Value(idColumn);
            var barcode = Value(barcodeColumn);
            if (string.IsNullOrWhiteSpace(name))
            {
                continue;
            }

            if (string.IsNullOrWhiteSpace(externalId))
            {
                externalId = barcode;
            }

            if (string.IsNullOrWhiteSpace(externalId))
            {
                continue;
            }

            var regular = ParseMoney(Value(regularColumn));
            var promotional = ParseMoney(Value(promoColumn));
            var unitPrice = ParseMoney(Value(unitPriceColumn));
            var quantity = ParseQuantity(Value(quantityColumn));
            var unit = NormalizeUnit(Value(unitColumn));
            var flags = new List<string>();
            if (string.IsNullOrWhiteSpace(barcode))
            {
                flags.Add("barcode_missing");
            }
            else if (!IsValidBarcode(barcode))
            {
                flags.Add("barcode_mismatch");
            }

            if (regular is null && promotional is null)
            {
                flags.Add("price_missing");
            }

            if ((unitPrice is not null && (unit is null || !IsKnownUnit(unit))) ||
                (quantity is not null && unit is null) || (unit is not null && !IsKnownUnit(unit)))
            {
                flags.Add("unit_inconsistent");
            }

            products.Add(new SourceProduct(externalId.Trim(), name.Trim(), Value(brandColumn), barcode,
                Value(categoryColumn), quantity, unit, regular, promotional, unitPrice, unit, flags));
        }

        return products;
    }

    private static string NormalizeHeader(string value)
    {
        var decomposed = value.Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(decomposed.Length);
        foreach (var character in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(character) != UnicodeCategory.NonSpacingMark && char.IsLetterOrDigit(character))
            {
                builder.Append(char.ToLowerInvariant(character));
            }
        }

        return builder.ToString();
    }

    private static char DetectDelimiter(string csv)
    {
        var end = csv.IndexOfAny(['\r', '\n']);
        var first = csv.AsSpan(0, end < 0 ? csv.Length : end);
        var semicolons = first.Count(';');
        var commas = first.Count(',');
        var tabs = first.Count('\t');
        return semicolons >= commas && semicolons >= tabs ? ';' : tabs > commas ? '\t' : ',';
    }

    private static List<List<string>> ParseRows(string text, char delimiter)
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
            else if ((character == '\r' || character == '\n') && !quoted)
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

    private static decimal? ParseMoney(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var cleaned = value.Trim().Replace("€", "", StringComparison.Ordinal).Replace("EUR", "", StringComparison.OrdinalIgnoreCase).Trim();
        var culture = cleaned.Contains(',') ? CultureInfo.GetCultureInfo("hr-HR") : CultureInfo.InvariantCulture;
        if (decimal.TryParse(cleaned, NumberStyles.Number, culture, out var local))
        {
            return local >= 0 ? local : null;
        }

        return decimal.TryParse(cleaned, NumberStyles.Number, CultureInfo.InvariantCulture, out var invariant) && invariant >= 0 ? invariant : null;
    }

    private static decimal? ParseQuantity(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var match = Regex.Match(value.Trim(), @"^[0-9]+(?:[.,][0-9]+)?");
        return match.Success ? ParseMoney(match.Value) : ParseMoney(value);
    }

    private static string? NormalizeUnit(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var unit = Regex.Replace(value.Trim().ToLowerInvariant(), @"^[0-9]+(?:[.,][0-9]+)?\s*", "");
        return unit switch
        {
            "kg" or "kilogram" or "kilogrami" => "kg",
            "g" or "gram" or "grami" => "g",
            "l" or "lit" or "litre" or "litra" => "l",
            "ml" or "mililitar" or "mililitara" => "ml",
            "kom" or "komad" or "komada" or "pcs" => "piece",
            _ => unit
        };
    }

    private static bool IsKnownUnit(string unit) => unit is "kg" or "g" or "l" or "ml" or "piece";

    private static bool IsValidBarcode(string barcode)
    {
        var digits = new string(barcode.Where(char.IsDigit).ToArray());
        if (digits.Length is not (8 or 12 or 13 or 14) || digits.Length != barcode.Count(char.IsDigit))
        {
            return false;
        }

        var sum = 0;
        var weight = 3;
        for (var index = digits.Length - 2; index >= 0; index--)
        {
            sum += (digits[index] - '0') * weight;
            weight = weight == 3 ? 1 : 3;
        }

        return (10 - sum % 10) % 10 == digits[^1] - '0';
    }
}

internal static class PublicArchive
{
    public static IReadOnlyList<(string Name, byte[] Content)> ReadEntries(byte[] archive, string extension)
    {
        using var stream = new MemoryStream(archive);
        using var zip = new ZipArchive(stream, ZipArchiveMode.Read);
        var entries = new List<(string Name, byte[] Content)>();
        long totalBytes = 0;
        foreach (var entry in zip.Entries.Where(entry => entry.FullName.EndsWith(extension, StringComparison.OrdinalIgnoreCase)))
        {
            if (entries.Count >= 1000 || entry.Length > 100_000_000 || totalBytes + entry.Length > 500_000_000)
            {
                throw new InvalidDataException("Public retailer archive exceeds the configured safety limits.");
            }

            using var input = entry.Open();
            using var output = new MemoryStream((int)entry.Length);
            input.CopyTo(output);
            var content = output.ToArray();
            totalBytes += content.Length;
            entries.Add((Path.GetFileName(entry.FullName), content));
        }

        return entries;
    }
}
