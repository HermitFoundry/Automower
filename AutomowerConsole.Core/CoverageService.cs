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
}

public record struct GpsPoint(double Lat, double Lon);

public record WorkAreaCoverage(long WorkAreaId, List<GpsPoint> Points);
