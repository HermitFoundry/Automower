namespace AutomowerConsole.Core;

// Storage seam for the mower catalog (mowers.json) - shared across all
// mowers, unlike IMowerRepository which is scoped to one. Kept as its own
// interface so a future SQLite "common" database (registry + whatever else
// isn't mower-specific) can implement this independently of the per-mower
// databases.
public interface IMowerRegistry
{
    List<StoredMower>? LoadMowers();
    void SaveMowers(IEnumerable<StoredMower> mowers);
}

public class JsonlMowerRegistry : IMowerRegistry
{
    public List<StoredMower>? LoadMowers() => Storage.LoadMowers();
    public void SaveMowers(IEnumerable<StoredMower> mowers) => Storage.SaveMowers(mowers);
}
