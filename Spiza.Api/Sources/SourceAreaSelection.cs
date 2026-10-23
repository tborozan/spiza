using Spiza.Api.Configuration;

namespace Spiza.Api.Sources;

internal static class SourceAreaSelection
{
    public static bool IsPrimaryCity(SourceStore store, SpizaOptions options)
    {
        var city = SpizaOptions.NormalizeAreaName(store.City);
        return city.Length > 0 && options.PrimaryCities.Any(area =>
            SpizaOptions.NormalizeAreaName(area) == city);
    }

    public static bool IsFallbackCity(SourceStore store, SpizaOptions options)
    {
        var city = SpizaOptions.NormalizeAreaName(store.City);
        return city.Length > 0 && !options.PrimaryCities.Any(area =>
                   SpizaOptions.NormalizeAreaName(area) == city) &&
               options.FallbackCities.Any(area => SpizaOptions.NormalizeAreaName(area) == city);
    }

    public static bool IsConfiguredCity(SourceStore store, SpizaOptions options) =>
        IsPrimaryCity(store, options) || IsFallbackCity(store, options);

    public static IReadOnlyList<T> Select<T>(
        IEnumerable<T> candidates,
        Func<T, SourceStore> getStore,
        SpizaOptions options)
    {
        var items = candidates.ToArray();
        var primaryCities = options.PrimaryCities
            .Select(SpizaOptions.NormalizeAreaName)
            .Where(city => city.Length > 0)
            .ToHashSet(StringComparer.Ordinal);
        var primary = items.Where(item => IsPrimaryCity(getStore(item), options)).ToArray();
        if (primary.Length > 0)
        {
            return primary;
        }

        foreach (var fallbackCity in options.FallbackCities.Select(SpizaOptions.NormalizeAreaName)
                     .Where(city => city.Length > 0 && !primaryCities.Contains(city))
                     .Distinct(StringComparer.Ordinal))
        {
            var fallback = items.Where(item =>
                IsFallbackCity(getStore(item), options) &&
                SpizaOptions.NormalizeAreaName(getStore(item).City) == fallbackCity).ToArray();
            if (fallback.Length > 0)
            {
                return fallback;
            }
        }

        return [];
    }
}
