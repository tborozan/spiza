using System.Globalization;
using System.Text.RegularExpressions;

namespace Spiza.Api.Sources;

internal static class ProductIdentity
{
    public static string? NormalizeBarcode(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var digits = new char[value.Length];
        var length = 0;
        foreach (var character in value.Trim())
        {
            if (character is >= '0' and <= '9')
            {
                digits[length++] = character;
            }
            else if (character is not ('-' or ' ' or '\t'))
            {
                return null;
            }
        }

        if (length is not (8 or 12 or 13 or 14))
        {
            return null;
        }

        return new string(digits, 0, length);
    }

    public static bool IsValidBarcode(string? value)
    {
        var digits = NormalizeBarcode(value);
        if (digits is null)
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

internal readonly record struct NormalizedMeasurement(
    decimal? Quantity,
    string? Unit,
    string? UnitPriceUnit,
    IReadOnlyList<string> QualityFlags);

internal static class ProductMeasurement
{
    private static readonly Regex QuantityPattern = new(
        @"^\s*([0-9]+(?:[.,][0-9]+)?)\s*([\p{L}.]+)?\s*$", RegexOptions.Compiled);
    private static readonly Regex UnitLabelPattern = new(
        @"^\s*([0-9]+(?:[.,][0-9]+)?)\s*([\p{L}.]+)\s*$", RegexOptions.Compiled);
    private static readonly Regex NamedPackageMeasurementPattern = new(
        @"(?<![\p{L}\d])([0-9]+(?:[.,][0-9]+)?)\s*(kg|g|ml|l)(?![\p{L}])",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public static NormalizedMeasurement Normalize(
        string? quantityText,
        string? unitText,
        string? productName = null,
        decimal? unitPrice = null,
        decimal? regularPrice = null,
        decimal? promotionalPrice = null)
    {
        var flags = new List<string>();
        var quantityMatch = string.IsNullOrWhiteSpace(quantityText) ? Match.Empty : QuantityPattern.Match(quantityText);
        decimal? sourceQuantity = quantityMatch.Success ? ParseDecimal(quantityMatch.Groups[1].Value) : null;
        var quantityUnit = quantityMatch.Success ? NormalizeUnit(quantityMatch.Groups[2].Value) : null;
        var namedPackageMeasurement = ParseNamedPackageMeasurement(productName);
        var namedPriceMatches = namedPackageMeasurement is not null && unitPrice is not null &&
            MatchesUnitPrice(namedPackageMeasurement.Value.Quantity, unitPrice.Value, regularPrice, promotionalPrice);
        var unitPriceLooksPerPackage = unitPrice is not null &&
            new[] { regularPrice, promotionalPrice }.Where(price => price is > 0)
                .Any(price => decimal.Round(price!.Value, 2, MidpointRounding.AwayFromZero) ==
                              decimal.Round(unitPrice.Value, 2, MidpointRounding.AwayFromZero));

        var labelMatch = string.IsNullOrWhiteSpace(unitText) ? Match.Empty : UnitLabelPattern.Match(unitText);
        var labelHasQuantity = labelMatch.Success;
        var labelQuantity = labelHasQuantity ? ParseDecimal(labelMatch.Groups[1].Value) : null;
        var labelUnit = labelHasQuantity ? NormalizeUnit(labelMatch.Groups[2].Value) : NormalizeUnit(unitText);

        decimal? normalizedQuantity = null;
        string? canonicalUnit = null;
        if (sourceQuantity is not null && quantityUnit is not null)
        {
            (normalizedQuantity, canonicalUnit) = ToCanonical(sourceQuantity.Value, quantityUnit);
            if (labelUnit is not null && labelUnit != canonicalUnit)
            {
                var namedMeasurementConfirmsQuantity = namedPackageMeasurement is not null &&
                    normalizedQuantity == namedPackageMeasurement.Value.Quantity &&
                    canonicalUnit == namedPackageMeasurement.Value.Unit;
                if (ToCanonical(1m, labelUnit).Unit == canonicalUnit ||
                    (labelHasQuantity && labelQuantity is not null &&
                    ToCanonical(labelQuantity.Value, labelUnit).Unit == canonicalUnit &&
                    ToCanonical(labelQuantity.Value, labelUnit).Quantity == normalizedQuantity) ||
                    (labelUnit == "piece" && namedMeasurementConfirmsQuantity) || namedPriceMatches)
                {
                    // Some feeds provide the numeric net quantity in base units and repeat the
                    // package quantity in the unit column (for example 0.068 and "68g").
                }
                else
                {
                    flags.Add("unit_inconsistent");
                }
            }
        }
        else if (sourceQuantity is not null && labelUnit is not null)
        {
            var labelCanonical = labelHasQuantity && labelQuantity is not null
                ? ToCanonical(labelQuantity.Value, labelUnit)
                : ((decimal?)null, (string?)null);
            var sourceMatchesLabelAmount = labelHasQuantity && labelQuantity == sourceQuantity;
            var sourceAlreadyCanonical = labelCanonical.Item1 == sourceQuantity && labelCanonical.Item2 is not null;

            if (sourceMatchesLabelAmount && labelCanonical.Item1 is not null)
            {
                normalizedQuantity = labelCanonical.Item1;
                canonicalUnit = labelCanonical.Item2;
            }
            else if (sourceAlreadyCanonical)
            {
                normalizedQuantity = sourceQuantity;
                canonicalUnit = labelCanonical.Item2;
            }
            else
            {
                (normalizedQuantity, canonicalUnit) = ToCanonical(sourceQuantity.Value, labelUnit);
                if (labelHasQuantity)
                {
                    flags.Add("unit_inconsistent");
                }
            }
        }
        else if (sourceQuantity is not null)
        {
            normalizedQuantity = sourceQuantity;
            canonicalUnit = quantityUnit;
        }
        else if (labelUnit is not null)
        {
            canonicalUnit = ToCanonical(1m, labelUnit).Unit;
        }

        if (namedPackageMeasurement is not null)
        {
            var sourceMatchesNamedQuantity = sourceQuantity == namedPackageMeasurement.Value.Quantity;
            if ((namedPriceMatches || sourceMatchesNamedQuantity && !unitPriceLooksPerPackage) &&
                (canonicalUnit is null or "piece" or "kg" or "l"))
            {
                // Some retailer feeds label a fractional net weight as a package count (for
                // example 0.068 + "piece" for a product named "68 g"). The explicit package
                // size and a matching per-unit price can recover the physical unit safely.
                normalizedQuantity = namedPackageMeasurement.Value.Quantity;
                canonicalUnit = namedPackageMeasurement.Value.Unit;
            }
            else if (canonicalUnit == namedPackageMeasurement.Value.Unit && normalizedQuantity is not null &&
                     normalizedQuantity != namedPackageMeasurement.Value.Quantity)
            {
                flags.Add("unit_inconsistent");
            }
            else if (canonicalUnit == "piece" && sourceQuantity is > 0 and < 1)
            {
                flags.Add("unit_inconsistent");
            }
        }

        if ((sourceQuantity is not null && canonicalUnit is null) ||
            (!string.IsNullOrWhiteSpace(unitText) && labelUnit is null) ||
            (labelUnit is not null && ToCanonical(1m, labelUnit).Unit is null))
        {
            flags.Add("unit_inconsistent");
        }

        return new NormalizedMeasurement(normalizedQuantity, canonicalUnit, canonicalUnit,
            flags.Distinct(StringComparer.OrdinalIgnoreCase).ToArray());
    }

    private static (decimal Quantity, string Unit)? ParseNamedPackageMeasurement(string? productName)
    {
        if (string.IsNullOrWhiteSpace(productName))
        {
            return null;
        }

        var match = NamedPackageMeasurementPattern.Match(productName);
        if (!match.Success || ParseDecimal(match.Groups[1].Value) is not decimal quantity || quantity <= 0)
        {
            return null;
        }

        var unit = NormalizeUnit(match.Groups[2].Value);
        if (unit is null)
        {
            return null;
        }

        var canonical = ToCanonical(quantity, unit);
        return canonical.Quantity is not null && canonical.Unit is not null
            ? (canonical.Quantity.Value, canonical.Unit)
            : null;
    }

    private static bool MatchesUnitPrice(decimal packageQuantity, decimal unitPrice, params decimal?[] prices) =>
        prices.Where(price => price is > 0)
            .Any(price => decimal.Round(price!.Value / packageQuantity, 2, MidpointRounding.AwayFromZero) ==
                          decimal.Round(unitPrice, 2, MidpointRounding.AwayFromZero));

    private static decimal? ParseDecimal(string value)
    {
        var culture = value.Contains(',') ? CultureInfo.GetCultureInfo("hr-HR") : CultureInfo.InvariantCulture;
        return decimal.TryParse(value, NumberStyles.Number, culture, out var parsed) && parsed >= 0 ? parsed : null;
    }

    private static string? NormalizeUnit(string? unit)
    {
        if (string.IsNullOrWhiteSpace(unit))
        {
            return null;
        }

        return unit.Trim().TrimEnd('.').ToLowerInvariant() switch
        {
            "kg" or "kilogram" or "kilogrami" => "kg",
            "g" or "gr" or "gram" or "grami" => "g",
            "l" or "lit" or "litre" or "litra" => "l",
            "ml" or "mililitar" or "mililitara" => "ml",
            "kom" or "komad" or "komada" or "pcs" or "piece" => "piece",
            _ => null
        };
    }

    private static (decimal? Quantity, string? Unit) ToCanonical(decimal quantity, string unit) => unit switch
    {
        "g" => (quantity / 1000m, "kg"),
        "ml" => (quantity / 1000m, "l"),
        "kg" or "l" or "piece" => (quantity, unit),
        _ => (null, null)
    };
}
