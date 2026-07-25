using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json.Serialization;

namespace AutomowerWeb;

public record WeatherInfo(double TemperatureCelsius, int WeatherCode, bool IsDay);

// Current weather for a mower's latest GPS position, via Open-Meteo's free
// forecast API - no API key, no signup. Singleton (see Program.cs) so the
// cache persists across dashboard loads; unlike LocationService's
// effectively-permanent cache, weather actually changes, so entries expire
// after CacheDuration instead of living forever.
public class WeatherService(IHttpClientFactory httpClientFactory)
{
    private static readonly TimeSpan CacheDuration = TimeSpan.FromMinutes(20);

    private readonly System.Collections.Concurrent.ConcurrentDictionary<(double Lat, double Lon), (DateTimeOffset FetchedAt, WeatherInfo? Weather)> _cache = new();

    public async Task<WeatherInfo?> GetCurrentWeatherAsync(double latitude, double longitude)
    {
        var key = (Math.Round(latitude, 2), Math.Round(longitude, 2));
        if (_cache.TryGetValue(key, out var cached) && DateTimeOffset.Now - cached.FetchedAt < CacheDuration)
        {
            return cached.Weather;
        }

        WeatherInfo? weather;
        try
        {
            using var http = httpClientFactory.CreateClient();
            var lat = latitude.ToString(CultureInfo.InvariantCulture);
            var lon = longitude.ToString(CultureInfo.InvariantCulture);
            var url = $"https://api.open-meteo.com/v1/forecast?latitude={lat}&longitude={lon}&current_weather=true";

            var response = await http.GetFromJsonAsync<OpenMeteoResponse>(url);
            weather = response?.CurrentWeather is { } cw
                ? new WeatherInfo(cw.Temperature, cw.WeatherCode, cw.IsDay == 1)
                : null;
        }
        catch
        {
            // Best-effort, same reasoning as LocationService - still cached
            // (with the normal TTL) so an outage doesn't retry every load.
            weather = null;
        }

        _cache[key] = (DateTimeOffset.Now, weather);
        return weather;
    }

    private record OpenMeteoResponse
    {
        [JsonPropertyName("current_weather")]
        public CurrentWeather? CurrentWeather { get; init; }
    }

    private record CurrentWeather
    {
        [JsonPropertyName("temperature")]
        public double Temperature { get; init; }

        [JsonPropertyName("weathercode")]
        public int WeatherCode { get; init; }

        [JsonPropertyName("is_day")]
        public int IsDay { get; init; }
    }
}
