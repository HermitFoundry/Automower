using System.Text.Json;

namespace AutomowerConsole.Core;

// Extracts real GPS coverage per work area from a mower's track log - used
// to visualize roughly what shape/area has actually been mowed, not a
// survey. Deliberately filters to activity == "MOWING" only: a poll taken
// while LEAVING/GOING_HOME still carries a workAreaId (whatever area is
// being traveled to/from), so without this filter, transit path points get
// mixed into a work area's own coverage cloud and drag any boundary
// computed from it out toward unrelated territory.
//
// Only `positions[0]` (the newest breadcrumb) is used per poll - NOT the
// rest of that poll's up-to-50-entry history. Investigated 2026-07-29 after
// the user spotted dots clearly outside their own work area on 3 different
// mowers: a poll's full breadcrumb array can still hold the tail end of the
// *previous* stay (e.g. several minutes parked near a charger in a
// different work area) for many polls after the mower has already moved on
// - confirmed by 3 real episodes where the outlier count decayed by exactly
// 2 per poll (46->44->42->...), the signature of a sliding 50-point buffer
// aging out stale history. A simple max-distance-from-position[0] cutoff
// doesn't work either - normal, uncontaminated within-poll spread is
// commonly 20-40m on its own (median ~24m across 1,477 real MOWING polls),
// so a radius tight enough to reject the stale clusters also strips out
// plenty of genuine data. `positions[0]` is always exactly synchronized
// with that same poll's own workAreaId, so it's the only entry in the
// array actually guaranteed correct - confirmed empirically: doing this
// dropped the outlier count to zero on real data. Costs density (roughly
// 1 point per poll instead of up to 50), not precision.
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

                if (!attributes.TryGetProperty("positions", out var positionsEl) ||
                    positionsEl.ValueKind != JsonValueKind.Array ||
                    positionsEl.GetArrayLength() == 0)
                {
                    continue;
                }

                var newest = positionsEl[0];
                if (!newest.TryGetProperty("latitude", out var latEl) || !newest.TryGetProperty("longitude", out var lonEl))
                {
                    continue;
                }

                if (!byArea.TryGetValue(workAreaId, out var set))
                {
                    set = [];
                    byArea[workAreaId] = set;
                }
                set.Add(new GpsPoint(latEl.GetDouble(), lonEl.GetDouble()));
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
