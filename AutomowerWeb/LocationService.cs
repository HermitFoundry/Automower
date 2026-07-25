using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json.Serialization;

namespace AutomowerWeb;

// Reverse-geocodes a mower's latest GPS position (from MowerAttributes.
// Positions[0], the newest breadcrumb) into a human place name, via
// OpenStreetMap's free Nominatim API - no API key, no signup. Registered
// as a singleton (see Program.cs) specifically so this in-memory cache
// actually persists across dashboard loads instead of resetting every
// request - a mower's charging station doesn't move meter-to-meter
// between polls, so there's no reason to re-geocode the same spot
// repeatedly, and Nominatim's usage policy explicitly asks for exactly
// this kind of caching (max ~1 request/second, no bulk/heavy use).
// ConcurrentDictionary since a singleton can be hit by concurrent
// dashboard loads from more than one browser/user.
public class LocationService(IHttpClientFactory httpClientFactory)
{
    private readonly System.Collections.Concurrent.ConcurrentDictionary<(double Lat, double Lon), string?> _cache = new();

    public async Task<string?> GetPlaceNameAsync(double latitude, double longitude)
    {
        // Rounded to ~1km precision - collapses GPS jitter around the same
        // charging station into one cache entry, and "which town" doesn't
        // need more precision than that anyway.
        var key = (Math.Round(latitude, 2), Math.Round(longitude, 2));
        if (_cache.TryGetValue(key, out var cached))
        {
            return cached;
        }

        string? name;
        try
        {
            using var http = httpClientFactory.CreateClient();
            http.DefaultRequestHeaders.UserAgent.ParseAdd("AutomowerConsole-Dashboard/1.0 (personal home project)");

            var lat = latitude.ToString(CultureInfo.InvariantCulture);
            var lon = longitude.ToString(CultureInfo.InvariantCulture);
            var url = $"https://nominatim.openstreetmap.org/reverse?format=jsonv2&lat={lat}&lon={lon}&zoom=12&accept-language=en";

            var response = await http.GetFromJsonAsync<NominatimResponse>(url);
            var place = response?.Address?.Town ?? response?.Address?.Village
                ?? response?.Address?.City ?? response?.Address?.Municipality
                ?? response?.Address?.County;
            var country = response?.Address?.Country;
            name = place is not null && country is not null ? $"{place}, {country}" : place ?? country;
        }
        catch
        {
            // Best-effort - a dashboard that can't reach Nominatim (or gets
            // a malformed response) shouldn't fail the whole page, just
            // show no location for this one mower. Still cached as null so
            // a persistent outage doesn't retry every single load.
            name = null;
        }

        _cache[key] = name;
        return name;
    }

    private record NominatimResponse
    {
        [JsonPropertyName("address")]
        public NominatimAddress? Address { get; init; }
    }

    private record NominatimAddress
    {
        [JsonPropertyName("town")] public string? Town { get; init; }
        [JsonPropertyName("village")] public string? Village { get; init; }
        [JsonPropertyName("city")] public string? City { get; init; }
        [JsonPropertyName("municipality")] public string? Municipality { get; init; }
        [JsonPropertyName("county")] public string? County { get; init; }
        [JsonPropertyName("country")] public string? Country { get; init; }
    }
}
