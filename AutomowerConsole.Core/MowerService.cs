namespace AutomowerConsole.Core;

// Mower listing, caching, and resolution - the "which mower(s) are we
// talking about" concern. Wraps AutomowerConnect for the live API and
// Storage for the local mowers.json cache. Prints its own diagnostics
// (ambiguous match, not found, fetching-from-API notices) since those are
// intrinsic to the resolution process itself, not a separate presentation
// layer on top of it.
public class MowerService
{
    public async Task<List<StoredMower>> RefreshMowersAsync()
    {
        var fetched = await AutomowerConnect.Instance.GetMowersAsync();
        var mowers = fetched
            .Select(m => new StoredMower(m.Id, m.Attributes.System.Name, m.Attributes.System.Model, m.Attributes.System.SerialNumber))
            .ToList();
        Storage.SaveMowers(mowers);
        return mowers;
    }

    public async Task<List<StoredMower>> EnsureMowersAsync()
    {
        var mowers = Storage.LoadMowers();
        if (mowers is null || mowers.Count == 0)
        {
            Console.WriteLine("No cached mower list found, fetching from API...");
            mowers = await RefreshMowersAsync();
        }
        return mowers;
    }

    // Matches by 1-based list index, exact id, exact name, or a unique
    // name-contains match. Returns candidates when the query is ambiguous.
    public (StoredMower? Match, List<StoredMower> Candidates) FindMower(List<StoredMower> mowers, string query)
    {
        if (int.TryParse(query, out var index) && index >= 1 && index <= mowers.Count)
        {
            return (mowers[index - 1], []);
        }

        var exact = mowers.FirstOrDefault(m => string.Equals(m.Id, query, StringComparison.OrdinalIgnoreCase))
            ?? mowers.FirstOrDefault(m => string.Equals(m.Name, query, StringComparison.OrdinalIgnoreCase));
        if (exact is not null)
        {
            return (exact, []);
        }

        var candidates = mowers.Where(m => m.Name.Contains(query, StringComparison.OrdinalIgnoreCase)).ToList();
        return candidates.Count == 1 ? (candidates[0], []) : (null, candidates);
    }

    // Resolves an explicit query (name/id/index) against the mower cache,
    // printing "no match"/"ambiguous" diagnostics itself. Shared by
    // ResolveMowerAsync's override-query branch and CommandUse, so that
    // diagnostic wording only lives in one place.
    public async Task<StoredMower?> ResolveExplicitMowerAsync(string query)
    {
        var mowers = await EnsureMowersAsync();
        var (match, candidates) = FindMower(mowers, query);

        if (match is not null)
        {
            return match;
        }

        if (candidates.Count > 1)
        {
            Console.WriteLine($"Multiple mowers match '{query}':");
            foreach (var c in candidates)
            {
                Console.WriteLine($"  - {c.Name} (id: {c.Id})");
            }
        }
        else
        {
            Console.WriteLine($"No mower found matching '{query}'. Run 'list' to see available mowers.");
        }
        return null;
    }

    // Resolves the mower to operate on: an explicit override query (name/id/index)
    // if given, otherwise the active mower from state.json. Prints its own error
    // and returns null when nothing can be resolved.
    public async Task<(string Id, string Name)?> ResolveMowerAsync(string? overrideQuery)
    {
        if (!string.IsNullOrWhiteSpace(overrideQuery))
        {
            var match = await ResolveExplicitMowerAsync(overrideQuery);
            return match is null ? null : (match.Id, match.Name);
        }

        var state = Storage.LoadState();
        if (state is null)
        {
            Console.WriteLine("No active mower set, and none specified. Use 'use <name|id|index>' first, or pass a mower name/id as an argument.");
            return null;
        }
        return (state.ActiveMowerId, state.ActiveMowerName);
    }
}
