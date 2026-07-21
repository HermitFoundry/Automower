using System.Text.Json;

static class Storage
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private static string BaseDir => AppContext.BaseDirectory;
    private static string ConfigPath => Path.Combine(BaseDir, "config.json");
    private static string MowersPath => Path.Combine(BaseDir, "mowers.json");
    private static string StatePath => Path.Combine(BaseDir, "state.json");
    private static string SchedulePath => Path.Combine(BaseDir, "schedule.json");

    public static Config LoadConfig()
    {
        if (!File.Exists(ConfigPath))
        {
            throw new FileNotFoundException($"Config file not found: {ConfigPath}");
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
        => File.WriteAllText(ConfigPath, JsonSerializer.Serialize(config, JsonOptions));

    public static void SaveMowers(IEnumerable<StoredMower> mowers)
        => File.WriteAllText(MowersPath, JsonSerializer.Serialize(mowers, JsonOptions));

    public static List<StoredMower>? LoadMowers()
    {
        if (!File.Exists(MowersPath)) return null;
        return JsonSerializer.Deserialize<List<StoredMower>>(File.ReadAllText(MowersPath));
    }

    public static void SaveState(ActiveState state)
        => File.WriteAllText(StatePath, JsonSerializer.Serialize(state, JsonOptions));

    public static ActiveState? LoadState()
    {
        if (!File.Exists(StatePath)) return null;
        return JsonSerializer.Deserialize<ActiveState>(File.ReadAllText(StatePath));
    }

    public static void SaveSchedules(Dictionary<string, MowerSchedule> schedules)
        => File.WriteAllText(SchedulePath, JsonSerializer.Serialize(schedules, JsonOptions));

    public static Dictionary<string, MowerSchedule> LoadSchedules()
    {
        if (!File.Exists(SchedulePath)) return [];
        return JsonSerializer.Deserialize<Dictionary<string, MowerSchedule>>(File.ReadAllText(SchedulePath)) ?? [];
    }
}
