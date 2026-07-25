using System.Text.Json.Serialization;

namespace AutomowerConsole.Core;

public record Config
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

// Wire DTOs below (TokenResponse through WorkAreaResourceData) stay internal
// to Core - only HusqvarnaClient/AutomowerConnect ever touch them directly;
// callers outside Core only ever see the unwrapped domain types further down.

internal record TokenResponse
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

internal record MowersResponse
{
    [JsonPropertyName("data")]
    public MowerData[] Data { get; init; } = [];
}

public record MowerData
{
    [JsonPropertyName("id")]
    public string Id { get; init; } = "";

    [JsonPropertyName("type")]
    public string Type { get; init; } = "";

    [JsonPropertyName("attributes")]
    public MowerAttributes Attributes { get; init; } = new();
}

public record MowerAttributes
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

    // GPS breadcrumb trail, newest first, capped at 50 entries - see
    // SKILL.md's Gotchas for the "not work-area boundary data" caveat.
    // Absent/empty when capabilities.position is false, or occasionally
    // even when true (e.g. a mower with a weak/no recent GPS fix).
    [JsonPropertyName("positions")]
    public PositionInfo[]? Positions { get; init; }

    [JsonPropertyName("capabilities")]
    public CapabilitiesInfo? Capabilities { get; init; }

    [JsonPropertyName("settings")]
    public SettingsInfo? Settings { get; init; }

    [JsonPropertyName("statistics")]
    public StatisticsInfo? Statistics { get; init; }
}

// Feature flags for this specific mower model - not settings a user
// changes, just what the hardware/firmware supports. Confirmed present on
// GET /mowers/{id} via a real 'status --all' dump (2026-07-25); optional
// here in case an older mower model's response omits it.
public record CapabilitiesInfo
{
    [JsonPropertyName("headlights")]
    public bool Headlights { get; init; }

    [JsonPropertyName("workAreas")]
    public bool WorkAreas { get; init; }

    [JsonPropertyName("position")]
    public bool Position { get; init; }

    [JsonPropertyName("canConfirmError")]
    public bool CanConfirmError { get; init; }

    [JsonPropertyName("stayOutZones")]
    public bool StayOutZones { get; init; }
}

public record SettingsInfo
{
    // A 1-9 dial value (per Home Assistant's husqvarna_automower
    // integration), NOT the same scale as WorkArea.CuttingHeight (a
    // percentage) - see that field's comment. Easy to conflate since both
    // are just called "cuttingHeight" in the raw API.
    [JsonPropertyName("cuttingHeight")]
    public int? CuttingHeight { get; init; }

    [JsonPropertyName("headlight")]
    public HeadlightInfo? Headlight { get; init; }
}

public record HeadlightInfo
{
    // ALWAYS_ON / ALWAYS_OFF / EVENING_ONLY / EVENING_AND_NIGHT, per
    // aioautomower's model_settings.py - not re-validated as a closed enum
    // here, displayed as-is (same "don't over-model an external API"
    // approach already used for Mower.Activity/State/etc.).
    [JsonPropertyName("mode")]
    public string Mode { get; init; } = "";
}

// Lifetime usage counters - confirmed via aioautomower's model_statistics.py
// docstrings: all *Time fields are seconds, TotalDriveDistance is meters,
// the two "number of" fields are unitless counts. Not the same as anything
// already modeled (battery/mower/metadata are current-state; this is
// cumulative since the mower was first set up, or since counters were last
// reset).
public record StatisticsInfo
{
    [JsonPropertyName("cuttingBladeUsageTime")]
    public long CuttingBladeUsageTime { get; init; }

    [JsonPropertyName("downTime")]
    public long DownTime { get; init; }

    [JsonPropertyName("numberOfChargingCycles")]
    public long NumberOfChargingCycles { get; init; }

    [JsonPropertyName("numberOfCollisions")]
    public long NumberOfCollisions { get; init; }

    [JsonPropertyName("totalChargingTime")]
    public long TotalChargingTime { get; init; }

    [JsonPropertyName("totalCuttingTime")]
    public long TotalCuttingTime { get; init; }

    [JsonPropertyName("totalDriveDistance")]
    public long TotalDriveDistance { get; init; }

    [JsonPropertyName("totalRunningTime")]
    public long TotalRunningTime { get; init; }

    [JsonPropertyName("totalSearchingTime")]
    public long TotalSearchingTime { get; init; }

    [JsonPropertyName("upTime")]
    public long UpTime { get; init; }
}

public record WorkArea
{
    [JsonPropertyName("workAreaId")]
    public long WorkAreaId { get; init; }

    [JsonPropertyName("name")]
    public string Name { get; init; } = "";

    [JsonPropertyName("type")]
    public string Type { get; init; } = "";

    // A PERCENTAGE (0-100) of the mower model's adjustable blade-height
    // range, per Home Assistant's husqvarna_automower integration
    // (its number entity for this uses native_unit_of_measurement
    // PERCENTAGE) - not a physical cm/mm measurement, and NOT the same
    // scale as SettingsInfo.CuttingHeight (the global setting, a 1-9 dial
    // value on that same integration). Confirmed as the source of a real
    // discrepancy: the Husqvarna app showed "5.5" (presumably converted to
    // cm using this mower model's actual min/max blade range) for a work
    // area this API reported as 87 (%) - that per-model min/max range
    // isn't exposed anywhere in this API, so the app's cm figure can't be
    // reproduced from this field; don't attempt to convert it, just label
    // it as a percentage.
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

public record CalendarInfo
{
    [JsonPropertyName("tasks")]
    public CalendarTask[] Tasks { get; init; } = [];
}

public record CalendarTask
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

public record PositionInfo
{
    [JsonPropertyName("latitude")]
    public double Latitude { get; init; }

    [JsonPropertyName("longitude")]
    public double Longitude { get; init; }
}

internal record WorkAreaResponse
{
    [JsonPropertyName("data")]
    public WorkAreaResourceData Data { get; init; } = new();
}

internal record WorkAreaResourceData
{
    [JsonPropertyName("id")]
    public string Id { get; init; } = "";

    [JsonPropertyName("type")]
    public string Type { get; init; } = "";

    [JsonPropertyName("attributes")]
    public WorkArea Attributes { get; init; } = new();
}

public record StayOutZonesInfo
{
    [JsonPropertyName("dirty")]
    public bool Dirty { get; init; }

    [JsonPropertyName("zones")]
    public StayOutZone[] Zones { get; init; } = [];
}

public record StayOutZone
{
    [JsonPropertyName("id")]
    public string Id { get; init; } = "";

    [JsonPropertyName("name")]
    public string Name { get; init; } = "";

    [JsonPropertyName("enabled")]
    public bool Enabled { get; init; }
}

public record MowerSystem
{
    [JsonPropertyName("name")]
    public string Name { get; init; } = "";

    [JsonPropertyName("model")]
    public string Model { get; init; } = "";

    [JsonPropertyName("serialNumber")]
    public long SerialNumber { get; init; }
}

public record BatteryInfo
{
    [JsonPropertyName("batteryPercent")]
    public int BatteryPercent { get; init; }

    // Seconds, per aioautomower's model_battery.py (deserializes via
    // timedelta(seconds=x)); 0 means "not currently charging/no estimate",
    // not "already full" - only meaningful while Mower.Activity is
    // CHARGING.
    [JsonPropertyName("remainingChargingTime")]
    public long RemainingChargingTime { get; init; }
}

public record MowerActivityState
{
    [JsonPropertyName("mode")]
    public string Mode { get; init; } = "";

    [JsonPropertyName("activity")]
    public string Activity { get; init; } = "";

    [JsonPropertyName("state")]
    public string State { get; init; } = "";

    // Which work area the mower is currently in - absent while parked at the
    // charger on some responses, present (and non-zero for a real named
    // area) while mowing. Independent of Mode: aioautomower/Home Assistant's
    // own integration tracks these as two separate attributes, not derived
    // from one another - observed live data on this account shows Mode
    // staying "MAIN_AREA" even while WorkAreaId points at a named, non-zero
    // custom work area, so Mode is not a reliable stand-in for "which area".
    [JsonPropertyName("workAreaId")]
    public long? WorkAreaId { get; init; }

    [JsonPropertyName("inactiveReason")]
    public string InactiveReason { get; init; } = "";

    [JsonPropertyName("errorCode")]
    public int ErrorCode { get; init; }
}

public record PlannerInfo
{
    // Unix epoch milliseconds; 0 means no scheduled start
    [JsonPropertyName("nextStartTimestamp")]
    public long NextStartTimestamp { get; init; }

    [JsonPropertyName("restrictedReason")]
    public string RestrictedReason { get; init; } = "";

    [JsonPropertyName("override")]
    public PlannerOverride? Override { get; init; }
}

public record PlannerOverride
{
    // NOT_ACTIVE / FORCE_PARK / FORCE_MOW, per aioautomower's
    // model_planner.py Actions enum - whether the app's schedule is
    // currently being manually overridden.
    [JsonPropertyName("action")]
    public string Action { get; init; } = "";
}

public record MetadataInfo
{
    [JsonPropertyName("connected")]
    public bool Connected { get; init; }

    // Unix epoch milliseconds
    [JsonPropertyName("statusTimestamp")]
    public long StatusTimestamp { get; init; }
}

internal record MowerResponse
{
    [JsonPropertyName("data")]
    public MowerData Data { get; init; } = new();
}

internal record MessagesResponse
{
    [JsonPropertyName("data")]
    public MessagesData Data { get; init; } = new();
}

internal record MessagesData
{
    [JsonPropertyName("id")]
    public string Id { get; init; } = "";

    [JsonPropertyName("type")]
    public string Type { get; init; } = "";

    [JsonPropertyName("attributes")]
    public MessagesAttributes Attributes { get; init; } = new();
}

internal record MessagesAttributes
{
    [JsonPropertyName("messages")]
    public MessageItem[] Messages { get; init; } = [];
}

public record MessageItem
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
public record StoredMower(string Id, string Name, string Model, long SerialNumber);

// Persisted to state.json
public record ActiveState(string ActiveMowerId, string ActiveMowerName);

// Persisted to schedule.json, keyed by mower id
public record MowerSchedule(string MowerName, DateTimeOffset FetchedAt, CalendarTask[] Tasks);
