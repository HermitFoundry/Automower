using System.Text.Json;
using System.Text.Json.Serialization;

namespace AutomowerWeb;

// Estimates a work area's cutting height in cm from the raw API percentage
// (WorkArea.CuttingHeight - see its doc comment in Models.cs) using a
// per-model min/max blade-height range, loaded from cutting-height-
// ranges.json (same directory as this file, copied to the build output -
// see the .csproj). The Husqvarna API never exposes that range anywhere,
// for any model - this table is NOT sourced from Husqvarna's own docs or
// the API itself, it's a third-party explanation (unverified, no official
// citation) of the linear percentage-to-cm mapping the mobile app appears
// to use internally. Treat any cm figure this produces as a best-effort
// estimate, not ground truth, and only trust it for models actually
// listed in that file - an unlisted model (or a missing/malformed file)
// falls back to showing the bare percentage, which is honest even if less
// informative, rather than guessing or crashing the page.
//
// A model with a manual (knob) height adjustment, not a motor - e.g. the
// 308V - has no way to report its actual physical height at all, so the
// API just returns a meaningless placeholder (observed: always 0) instead
// of the real value. Converting that "0" through the percentage formula
// would print a plausible-looking but simply wrong "2.0 cm", so
// "electronic": false in the ranges file deliberately skips the
// conversion instead.
//
// To add a mower model: add an entry to cutting-height-ranges.json (key =
// a substring that appears in that mower's System.Model, e.g. "450X") -
// no code change or rebuild needed if the file is edited directly in the
// build output; a source-tree edit needs a rebuild to be copied there.
public static class CuttingHeightEstimator
{
    private static readonly Dictionary<string, Range> Ranges = LoadRanges();

    private static Dictionary<string, Range> LoadRanges()
    {
        try
        {
            var path = Path.Combine(AppContext.BaseDirectory, "cutting-height-ranges.json");
            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<Dictionary<string, Range>>(
                json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? [];
        }
        catch
        {
            // Missing/malformed file - every model just falls back to the
            // bare-percentage label below, rather than crashing the whole
            // page over a reference-data file.
            return [];
        }
    }

    public static string Label(int percentage, string model)
    {
        var match = Ranges.FirstOrDefault(kv => model.Contains(kv.Key, StringComparison.OrdinalIgnoreCase));
        if (match.Key is null)
        {
            return $"{percentage}% of range";
        }

        var range = match.Value;
        if (!range.Electronic)
        {
            return "Manual adj.";
        }

        var cm = range.MinCm + percentage / 100.0 * (range.MaxCm - range.MinCm);
        return $"{percentage}% (≈{cm:F1} cm)";
    }

    private record Range
    {
        [JsonPropertyName("minCm")]
        public double MinCm { get; init; }

        [JsonPropertyName("maxCm")]
        public double MaxCm { get; init; }

        [JsonPropertyName("electronic")]
        public bool Electronic { get; init; }
    }
}
