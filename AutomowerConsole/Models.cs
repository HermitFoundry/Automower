using System.Text.Json.Serialization;

namespace AutomowerConsole;

record Config
{
    public string AppKey { get; init; } = "";
    public string AppSecret { get; init; } = "";

    // 'track' polling intervals, in seconds. Overridable per-run for the
    // scheduled/active one via 'track [seconds]'.
    public int ScheduledIntervalSeconds { get; init; } = 60;
    public int IdleIntervalSeconds { get; init; } = 300;
    public int NightIntervalSeconds { get; init; } = 1800;

    // Nighttime window, hour-of-day 0-23, wraps past midnight (22 -> 8).
    // No manual mowing start is expected in this window.
    public int NightStartHour { get; init; } = 22;
    public int NightEndHour { get; init; } = 8;
}

record TokenResponse
{
    [JsonPropertyName("access_token")]
    public string AccessToken { get; init; } = "";

    [JsonPropertyName("token_type")]
    public string TokenType { get; init; } = "";

    [JsonPropertyName("expires_in")]
    public int ExpiresIn { get; init; }

    [JsonPropertyName("provider")]
    public string Provider { get; init; } = "";
}

record MowersResponse
{
    [JsonPropertyName("data")]
    public MowerData[] Data { get; init; } = [];
}

record MowerData
{
    [JsonPropertyName("id")]
    public string Id { get; init; } = "";

    [JsonPropertyName("type")]
    public string Type { get; init; } = "";

    [JsonPropertyName("attributes")]
    public MowerAttributes Attributes { get; init; } = new();
}

record MowerAttributes
{
    [JsonPropertyName("system")]
    public MowerSystem System { get; init; } = new();

    [JsonPropertyName("battery")]
    public BatteryInfo Battery { get; init; } = new();

    [JsonPropertyName("mower")]
    public MowerActivityState Mower { get; init; } = new();

    [JsonPropertyName("planner")]
    public PlannerInfo Planner { get; init; } = new();

    [JsonPropertyName("metadata")]
    public MetadataInfo Metadata { get; init; } = new();

    [JsonPropertyName("workAreas")]
    public WorkArea[]? WorkAreas { get; init; }

    [JsonPropertyName("stayOutZones")]
    public StayOutZonesInfo? StayOutZones { get; init; }

    [JsonPropertyName("calendar")]
    public CalendarInfo? Calendar { get; init; }
}

record WorkArea
{
    [JsonPropertyName("workAreaId")]
    public long WorkAreaId { get; init; }

    [JsonPropertyName("name")]
    public string Name { get; init; } = "";

    [JsonPropertyName("type")]
    public string Type { get; init; } = "";

    [JsonPropertyName("cuttingHeight")]
    public int CuttingHeight { get; init; }

    [JsonPropertyName("enabled")]
    public bool Enabled { get; init; }

    // Unix epoch milliseconds; 0 means never
    [JsonPropertyName("lastTimeAbandoned")]
    public long LastTimeAbandoned { get; init; }

    [JsonPropertyName("useGlobalCuttingHeight")]
    public bool UseGlobalCuttingHeight { get; init; }

    // Only present on GET /mowers/{id}/workAreas/{workAreaId}, not on the
    // workAreas[] embedded in GET /mowers/{id}
    [JsonPropertyName("calendar")]
    public CalendarInfo? Calendar { get; init; }
}

record CalendarInfo
{
    [JsonPropertyName("tasks")]
    public CalendarTask[] Tasks { get; init; } = [];
}

record CalendarTask
{
    // Minutes from midnight
    [JsonPropertyName("start")]
    public int Start { get; init; }

    // Minutes
    [JsonPropertyName("duration")]
    public int Duration { get; init; }

    [JsonPropertyName("monday")]
    public bool Monday { get; init; }

    [JsonPropertyName("tuesday")]
    public bool Tuesday { get; init; }

    [JsonPropertyName("wednesday")]
    public bool Wednesday { get; init; }

    [JsonPropertyName("thursday")]
    public bool Thursday { get; init; }

    [JsonPropertyName("friday")]
    public bool Friday { get; init; }

    [JsonPropertyName("saturday")]
    public bool Saturday { get; init; }

    [JsonPropertyName("sunday")]
    public bool Sunday { get; init; }

    [JsonPropertyName("workAreaId")]
    public long? WorkAreaId { get; init; }
}

record WorkAreaResponse
{
    [JsonPropertyName("data")]
    public WorkAreaResourceData Data { get; init; } = new();
}

record WorkAreaResourceData
{
    [JsonPropertyName("id")]
    public string Id { get; init; } = "";

    [JsonPropertyName("type")]
    public string Type { get; init; } = "";

    [JsonPropertyName("attributes")]
    public WorkArea Attributes { get; init; } = new();
}

record StayOutZonesInfo
{
    [JsonPropertyName("dirty")]
    public bool Dirty { get; init; }

    [JsonPropertyName("zones")]
    public StayOutZone[] Zones { get; init; } = [];
}

record StayOutZone
{
    [JsonPropertyName("id")]
    public string Id { get; init; } = "";

    [JsonPropertyName("name")]
    public string Name { get; init; } = "";

    [JsonPropertyName("enabled")]
    public bool Enabled { get; init; }
}

record MowerSystem
{
    [JsonPropertyName("name")]
    public string Name { get; init; } = "";

    [JsonPropertyName("model")]
    public string Model { get; init; } = "";

    [JsonPropertyName("serialNumber")]
    public long SerialNumber { get; init; }
}

record BatteryInfo
{
    [JsonPropertyName("batteryPercent")]
    public int BatteryPercent { get; init; }
}

record MowerActivityState
{
    [JsonPropertyName("mode")]
    public string Mode { get; init; } = "";

    [JsonPropertyName("activity")]
    public string Activity { get; init; } = "";

    [JsonPropertyName("state")]
    public string State { get; init; } = "";

    [JsonPropertyName("inactiveReason")]
    public string InactiveReason { get; init; } = "";

    [JsonPropertyName("errorCode")]
    public int ErrorCode { get; init; }
}

record PlannerInfo
{
    // Unix epoch milliseconds; 0 means no scheduled start
    [JsonPropertyName("nextStartTimestamp")]
    public long NextStartTimestamp { get; init; }

    [JsonPropertyName("restrictedReason")]
    public string RestrictedReason { get; init; } = "";
}

record MetadataInfo
{
    [JsonPropertyName("connected")]
    public bool Connected { get; init; }

    // Unix epoch milliseconds
    [JsonPropertyName("statusTimestamp")]
    public long StatusTimestamp { get; init; }
}

record MowerResponse
{
    [JsonPropertyName("data")]
    public MowerData Data { get; init; } = new();
}

record MessagesResponse
{
    [JsonPropertyName("data")]
    public MessagesData Data { get; init; } = new();
}

record MessagesData
{
    [JsonPropertyName("id")]
    public string Id { get; init; } = "";

    [JsonPropertyName("type")]
    public string Type { get; init; } = "";

    [JsonPropertyName("attributes")]
    public MessagesAttributes Attributes { get; init; } = new();
}

record MessagesAttributes
{
    [JsonPropertyName("messages")]
    public MessageItem[] Messages { get; init; } = [];
}

record MessageItem
{
    // Unix epoch seconds
    [JsonPropertyName("time")]
    public long Time { get; init; }

    [JsonPropertyName("code")]
    public int Code { get; init; }

    [JsonPropertyName("severity")]
    public string Severity { get; init; } = "";

    [JsonPropertyName("latitude")]
    public double? Latitude { get; init; }

    [JsonPropertyName("longitude")]
    public double? Longitude { get; init; }
}

// Persisted to mowers.json
record StoredMower(string Id, string Name, string Model, long SerialNumber);

// Persisted to state.json
record ActiveState(string ActiveMowerId, string ActiveMowerName);

// Persisted to schedule.json, keyed by mower id
record MowerSchedule(string MowerName, DateTimeOffset FetchedAt, CalendarTask[] Tasks);
