using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json.Serialization;

namespace AutomowerWeb;

// Satellite imagery background for the coverage map, via Esri's free,
// keyless World Imagery sample server (server.arcgisonline.com/.../
// World_Imagery/MapServer/export) - no API key or Google-Cloud-style
// billing account, unlike Google's Static Maps API. Registered as a
// singleton (see Program.cs), same reasoning as LocationService: a mower's
// coverage bounding box only grows slowly over a season, so there's no
// reason to re-probe the same box on every dashboard load.
//
// The export endpoint has a real, undocumented resolution ceiling that
// varies by location - confirmed 2026-07-30 by direct testing: it hard-
// errors (HTTP 500, "Error: bytes") once asked for finer detail than it has
// cached tiles for at that spot, with no graceful degradation to a coarser
// image. Observed ceilings ranged from ~8 px/meter (Asker, Norway) down to
// ~5 px/meter (rural Piteå, Sweden) for this account's real mowers - there's
// no way to know the right number for a new location in advance, and even
// the best case is well short of what Google's satellite imagery shows for
// the same rural addresses (a real data-source quality gap, not something
// tunable away). So: probe a descending list of candidates using the cheap
// f=json response form instead of guessing one fixed number that's either
// too conservative everywhere or fails somewhere - a failed probe returns a
// tiny JSON body with an empty "href" and zeroed width/height, a successful
// one a real href/dimensions, confirmed against both a real success and a
// real failure for the same bbox.
public class SatelliteImageService(IHttpClientFactory httpClientFactory)
{
    private const string ExportBaseUrl = "https://server.arcgisonline.com/ArcGIS/rest/services/World_Imagery/MapServer/export";

    // Highest detail this endpoint has ever been observed to serve, down to
    // the floor confirmed safe everywhere tested (2026-07-30).
    private static readonly double[] CandidatePixelsPerMeter = [15, 10, 8, 6, 5, 4];

    private readonly ConcurrentDictionary<(double LonMin, double LatMin, double LonMax, double LatMax), string?> _cache = new();

    public async Task<string?> GetImageUrlAsync(double lonMin, double latMin, double lonMax, double latMax, double widthMeters, double heightMeters)
    {
        var key = (Math.Round(lonMin, 6), Math.Round(latMin, 6), Math.Round(lonMax, 6), Math.Round(latMax, 6));
        if (_cache.TryGetValue(key, out var cached))
        {
            return cached;
        }

        var bbox = string.Create(CultureInfo.InvariantCulture, $"{lonMin:F7},{latMin:F7},{lonMax:F7},{latMax:F7}");
        string? result = null;
        try
        {
            using var http = httpClientFactory.CreateClient();
            foreach (var pxPerMeter in CandidatePixelsPerMeter)
            {
                var pxWidth = Math.Max(1, (int)Math.Round(widthMeters * pxPerMeter));
                var pxHeight = Math.Max(1, (int)Math.Round(heightMeters * pxPerMeter));
                var probeUrl = $"{ExportBaseUrl}?bbox={bbox}&bboxSR=4326&imageSR=4326&size={pxWidth},{pxHeight}&format=jpg&f=json";

                var probe = await http.GetFromJsonAsync<ExportProbeResponse>(probeUrl);
                if (!string.IsNullOrEmpty(probe?.Href))
                {
                    // Don't embed Esri's own returned href directly - it
                    // points at a temp file in their output directory that
                    // gets cleaned up on their own schedule, not safe to
                    // cache and reuse across future page loads. Rebuild the
                    // same request with f=image instead, which regenerates
                    // the image fresh on every fetch.
                    result = $"{ExportBaseUrl}?bbox={bbox}&bboxSR=4326&imageSR=4326&size={pxWidth},{pxHeight}&format=jpg&f=image";
                    break;
                }
            }
        }
        catch
        {
            // Best-effort - the coverage map already renders fine with no
            // background image (MowerDetails.razor skips the <image>
            // element when this is null), same fallback as any other reason
            // the imagery might be unavailable.
            result = null;
        }

        _cache[key] = result;
        return result;
    }

    private record ExportProbeResponse
    {
        [JsonPropertyName("href")] public string? Href { get; init; }
    }
}
