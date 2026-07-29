using System.Text.Json;

namespace AutomowerConsole.Core;

// Extracts real GPS coverage per work area from a mower's track log - used
// to visualize roughly what shape/area has actually been mowed, not a
// survey. Deliberately filters to activity == "MOWING" only: a poll taken
// while LEAVING/GOING_HOME still carries a workAreaId (whatever area is
// being traveled to/from), so without this filter, transit path points get
// mixed into a work area's own coverage cloud and drag any boundary
// computed from it out toward unrelated territory - confirmed as the root
// cause of a bad first attempt at this, 2026-07-29 (a one-off Python script
// grouping by workAreaId alone, no activity filter).
public class CoverageService
{
    public List<WorkAreaCoverage> GetCoverageByWorkArea(string mowerName)
    {
        var logPath = Storage.GetTrackLogPath(mowerName);
        if (!File.Exists(logPath))
        {
            return [];
        }

        var byArea = new Dictionary<long, HashSet<GpsPoint>>();

        foreach (var line in File.ReadLines(logPath))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            try
            {
                using var doc = JsonDocument.Parse(line);
                var attributes = doc.RootElement.GetProperty("response").GetProperty("data").GetProperty("attributes");
                var mowerObj = attributes.GetProperty("mower");
                var activity = mowerObj.GetProperty("activity").GetString();
                if (activity != "MOWING")
                {
                    continue;
                }

                var workAreaId = mowerObj.TryGetProperty("workAreaId", out var waIdEl) ? waIdEl.GetInt64() : 0L;

                if (!attributes.TryGetProperty("positions", out var positionsEl) || positionsEl.ValueKind != JsonValueKind.Array)
                {
                    continue;
                }

                if (!byArea.TryGetValue(workAreaId, out var set))
                {
                    set = [];
                    byArea[workAreaId] = set;
                }

                foreach (var pos in positionsEl.EnumerateArray())
                {
                    if (pos.TryGetProperty("latitude", out var latEl) && pos.TryGetProperty("longitude", out var lonEl))
                    {
                        set.Add(new GpsPoint(latEl.GetDouble(), lonEl.GetDouble()));
                    }
                }
            }
            catch (Exception)
            {
                // Same tolerance as SummarizeSessions - skip a malformed line rather than fail the whole read.
            }
        }

        return byArea
            .Select(kv => new WorkAreaCoverage(kv.Key, kv.Value.ToList()))
            .OrderByDescending(c => c.Points.Count)
            .ToList();
    }
}

public record struct GpsPoint(double Lat, double Lon);

public record WorkAreaCoverage(long WorkAreaId, List<GpsPoint> Points);
