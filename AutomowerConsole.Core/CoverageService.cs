namespace AutomowerConsole.Core;

// Extracts real GPS coverage per work area from a mower's poll history -
// used to visualize roughly what shape/area has actually been mowed, not a
// survey. Deliberately filters to activity == "MOWING" only: a poll taken
// while LEAVING/GOING_HOME still carries a workAreaId (whatever area is
// being traveled to/from), so without this filter, transit path points get
// mixed into a work area's own coverage cloud and drag any boundary
// computed from it out toward unrelated territory.
//
// The positions[0]-only breadcrumb selection (why the rest of a poll's
// up-to-50-entry array isn't used) lives in JsonlMowerRepository.GetHistory()
// now - see its comment for the full investigation.
//
// Second filter, added once hybrid-track mixed real dots from other areas
// into a work area's own cloud (e.g. AM430X NERA's "oversiden" showing
// green/violet/brown together): under REST-only polling every poll's
// WorkAreaId and Latitude/Longitude always came from the same atomic
// snapshot, so pairing them was always safe - that guarantee is exactly
// what MaxWorkAreaIdAge restores. A hybrid-tracked, WebSocket-event-sourced
// poll doesn't have that guarantee - a position-event-v2 row's WorkAreaId
// is carried forward from whenever the last mower-event-v2 actually set it
// (see PollRecord.WorkAreaIdObservedAt), which can be stale enough to span
// a real work-area change. Skipping a point whose WorkAreaId is too old
// relative to its own timestamp trades some coverage density for not
// mislabeling points into the wrong area's color.
public class CoverageService(IMowerRepositoryFactory repositoryFactory)
{
    private static readonly TimeSpan MaxWorkAreaIdAge = TimeSpan.FromMinutes(5);

    public List<WorkAreaCoverage> GetCoverageByWorkArea(string mowerName)
    {
        var history = repositoryFactory.ForMower(mowerName).GetHistory();
        var byArea = new Dictionary<long, HashSet<GpsPoint>>();

        foreach (var poll in history.Polls)
        {
            if (poll.Activity != "MOWING" || poll.Latitude is null || poll.Longitude is null)
            {
                continue;
            }

            if (poll.Timestamp - poll.WorkAreaIdObservedAt > MaxWorkAreaIdAge)
            {
                continue;
            }

            if (!byArea.TryGetValue(poll.WorkAreaId, out var set))
            {
                set = [];
                byArea[poll.WorkAreaId] = set;
            }
            set.Add(new GpsPoint(poll.Latitude.Value, poll.Longitude.Value));
        }

        return byArea
            .Select(kv => new WorkAreaCoverage(kv.Key, kv.Value.ToList()))
            .OrderByDescending(c => c.Points.Count)
            .ToList();
    }

    // Only the most recent LEAVING run and the most recent GOING_HOME run -
    // not every transit trip ever recorded. Every time a new contiguous run
    // of one of these activities starts, it replaces whatever was
    // previously stored for that same activity (see the `result[...] = run`
    // reassignment below - by the end of the scan only the last run per
    // activity survives). Deliberately not accumulated across history: a
    // transit path can vary trip to trip, and drawing every past one would
    // turn into unreadable clutter on top of the per-work-area dot clouds
    // this map already shows - one current "here's how it gets there" line
    // per direction is what was actually asked for.
    //
    // Points are an ordered List, not a HashSet like GetCoverageByWorkArea's
    // per-area sets - a line needs its points in the order the mower
    // actually visited them, duplicates included.
    public List<TransportPath> GetLatestTransportPaths(string mowerName)
    {
        var history = repositoryFactory.ForMower(mowerName).GetHistory();
        var result = new Dictionary<string, List<(DateTimeOffset Timestamp, GpsPoint Point)>>();
        string? currentActivity = null;
        List<(DateTimeOffset, GpsPoint)>? currentRun = null;

        foreach (var poll in history.Polls)
        {
            var isTransport = poll.Activity is "LEAVING" or "GOING_HOME";
            if (!isTransport || poll.Latitude is null || poll.Longitude is null)
            {
                currentActivity = null;
                currentRun = null;
                continue;
            }

            if (poll.Activity != currentActivity)
            {
                currentActivity = poll.Activity;
                currentRun = [];
                result[poll.Activity] = currentRun;
            }
            currentRun!.Add((poll.Timestamp, new GpsPoint(poll.Latitude.Value, poll.Longitude.Value)));
        }

        return result.Select(kv => new TransportPath(kv.Key, TrimAfterImplausibleJump(kv.Value))).ToList();
    }

    // Same speed-based anomaly check already validated against this
    // account's real mowing data (2026-07-29 - observed speeds topped out
    // under 0.5 m/s across all three mowers, 1.5 m/s was chosen as a
    // deliberately generous 3x margin with zero false positives) - reused
    // here rather than inventing a new number. Once a jump is seen,
    // everything from that point onward in the run is dropped rather than
    // just the one point, since a GPS fix degraded by multipath (a
    // charging dock is often close to a building) tends to stay degraded
    // for the rest of that approach, not self-correct mid-run.
    //
    // Confirmed against a real case (AM430X NERA, 2026-07-31 GOING_HOME
    // trip) that this doesn't catch every gap between a transit line and
    // the current-position dot: every recorded step in that run stayed
    // under 0.44 m/s, well under this threshold - the ~8.5m difference
    // there turned out to be the mower's GPS reading drifting while
    // sitting still at the dock for the better part of an hour afterward
    // (a few centimeters per minute - no single step fast enough for any
    // reasonable speed threshold to flag), not a jump during the trip
    // itself. Kept anyway: a real, precedented protection against actual
    // in-transit GPS jumps, just not a fix for slow drift after arrival.
    private const double MaxPlausibleSpeedMetersPerSecond = 1.5;

    private static List<GpsPoint> TrimAfterImplausibleJump(List<(DateTimeOffset Timestamp, GpsPoint Point)> run)
    {
        var kept = new List<GpsPoint>(run.Count) { run[0].Point };
        for (var i = 1; i < run.Count; i++)
        {
            var seconds = (run[i].Timestamp - run[i - 1].Timestamp).TotalSeconds;
            if (seconds > 0 && DistanceMeters(run[i - 1].Point, run[i].Point) / seconds > MaxPlausibleSpeedMetersPerSecond)
            {
                break;
            }
            kept.Add(run[i].Point);
        }
        return kept;
    }

    private static double DistanceMeters(GpsPoint a, GpsPoint b)
    {
        const double MetersPerDegLat = 111320.0;
        var metersPerDegLon = MetersPerDegLat * Math.Cos((a.Lat + b.Lat) / 2.0 * Math.PI / 180.0);
        var dx = (b.Lon - a.Lon) * metersPerDegLon;
        var dy = (b.Lat - a.Lat) * MetersPerDegLat;
        return Math.Sqrt((dx * dx) + (dy * dy));
    }
}

public record struct GpsPoint(double Lat, double Lon);

public record WorkAreaCoverage(long WorkAreaId, List<GpsPoint> Points);

public record TransportPath(string Activity, List<GpsPoint> Points);
