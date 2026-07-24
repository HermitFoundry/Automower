using System.Text.Json;

namespace AutomowerConsole.Core;

// Public: unlike AutomowerConnect/HusqvarnaClient (internal - nothing
// outside Core should make API calls directly), the CLI's config/state
// commands (CommandConfig, CommandUse, CommandCurrent in Program.cs) read
// and write config.json/state.json directly, without a service layer of
// their own - an accepted design decision from the earlier service-layer
// pass, not something this extraction changes.
public static class Storage
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    // Anchored to the repo root (found by walking up from the running
    // executable to the nearest .slnx - there's only ever one, and it lives
    // in the true repo root, unlike .csproj which now sits one level down
    // in AutomowerConsole/), not the build output folder - AppContext.
    // BaseDirectory is AutomowerConsole/bin/<Config>/<TFM>/ and gets wiped
    // by 'dotnet clean', which would otherwise silently discard config/state.
    private static readonly string RepoRoot = FindRepoRoot();

    private static string ConfigDir => Path.Combine(RepoRoot, ".config");
    private static string DataDir => Path.Combine(RepoRoot, ".data");

    private static string ConfigPath => Path.Combine(ConfigDir, "config.json");
    private static string MowersPath => Path.Combine(DataDir, "mowers.json");
    private static string StatePath => Path.Combine(DataDir, "state.json");
    private static string SchedulePath => Path.Combine(DataDir, "schedule.json");

    // One log file per mower - deliberately no combined view, per-mower is
    // the only thing that's ever wanted.
    public static string GetTrackLogPath(string mowerName)
        => Path.Combine(DataDir, $"track-{SanitizeForFileName(mowerName)}.jsonl");

    private static string SanitizeForFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var chars = name.Select(c => invalid.Contains(c) || c == ' ' ? '-' : c).ToArray();
        var result = new string(chars);
        while (result.Contains("--")) result = result.Replace("--", "-");
        return result.Trim('-');
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (dir.GetFiles("*.slnx").Length > 0)
            {
                return dir.FullName;
            }
            dir = dir.Parent;
        }
        // No .slnx found walking up (e.g. a bare publish without source) -
        // fall back to sitting next to the executable, as before.
        return AppContext.BaseDirectory;
    }

    public static void EnsureDataDir() => Directory.CreateDirectory(DataDir);

    public static Config LoadConfig()
    {
        if (!File.Exists(ConfigPath))
        {
            throw new FileNotFoundException(
                $"Config file not found: {ConfigPath}. Run 'config AppKey=... AppSecret=...' to create it.");
        }

        var config = JsonSerializer.Deserialize<Config>(File.ReadAllText(ConfigPath))
                     ?? throw new InvalidOperationException("Failed to parse config.json");

        if (string.IsNullOrWhiteSpace(config.AppKey) || config.AppKey == "YOUR_APP_KEY_HERE")
        {
            throw new InvalidOperationException("AppKey is not set in config.json");
        }
        if (string.IsNullOrWhiteSpace(config.AppSecret) || config.AppSecret == "YOUR_APP_SECRET_HERE")
        {
            throw new InvalidOperationException("AppSecret is not set in config.json");
        }

        return config;
    }

    // Unlike LoadConfig(), doesn't require AppKey/AppSecret to already be
    // set - used by the 'config' command to build up config.json from scratch.
    public static Config LoadConfigForEditing()
    {
        if (!File.Exists(ConfigPath)) return new Config();
        return JsonSerializer.Deserialize<Config>(File.ReadAllText(ConfigPath)) ?? new Config();
    }

    public static void SaveConfig(Config config)
    {
        Directory.CreateDirectory(ConfigDir);
        File.WriteAllText(ConfigPath, JsonSerializer.Serialize(config, JsonOptions));
    }

    public static void SaveMowers(IEnumerable<StoredMower> mowers)
    {
        EnsureDataDir();
        File.WriteAllText(MowersPath, JsonSerializer.Serialize(mowers, JsonOptions));
    }

    public static List<StoredMower>? LoadMowers()
    {
        if (!File.Exists(MowersPath)) return null;
        return JsonSerializer.Deserialize<List<StoredMower>>(File.ReadAllText(MowersPath));
    }

    public static void SaveState(ActiveState state)
    {
        EnsureDataDir();
        File.WriteAllText(StatePath, JsonSerializer.Serialize(state, JsonOptions));
    }

    public static ActiveState? LoadState()
    {
        if (!File.Exists(StatePath)) return null;
        return JsonSerializer.Deserialize<ActiveState>(File.ReadAllText(StatePath));
    }

    public static void SaveSchedules(Dictionary<string, MowerSchedule> schedules)
    {
        EnsureDataDir();
        File.WriteAllText(SchedulePath, JsonSerializer.Serialize(schedules, JsonOptions));
    }

    public static Dictionary<string, MowerSchedule> LoadSchedules()
    {
        if (!File.Exists(SchedulePath)) return [];
        return JsonSerializer.Deserialize<Dictionary<string, MowerSchedule>>(File.ReadAllText(SchedulePath)) ?? [];
    }
}