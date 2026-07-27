using AutomowerConsole.Core;

namespace AutomowerWeb;

// Presentation-only mapping from raw API activity/connection state to an
// icon/CSS class/friendly label. Deliberately not in AutomowerConsole.Core -
// same reasoning as Program.cs's own DescribeActivity staying in the CLI:
// this is a UI concern of whichever layer is doing the rendering, not
// domain logic shared between consumers.
public static class MowerDisplay
{
    public static string Icon(string activity, bool connected) => !connected
        ? "⚠️"
        : activity switch
        {
            "MOWING" => "🌱",
            "CHARGING" => "🔋",
            "PARKED_IN_CS" => "🏠",
            // Same icon as LEAVING, deliberately - GOING_HOME is the same
            // vehicle heading the opposite direction, not a different
            // activity in spirit. Visually mirrored via the "icon-flip" CSS
            // class (see IconClass) rather than a different emoji, since
            // there's no separate "tractor facing right" glyph to reach for.
            "GOING_HOME" => "🚜",
            "LEAVING" => "🚜",
            "STOPPED_IN_GARDEN" => "⏸️",
            _ => "❔",
        };

    // Pairs with Icon(): GOING_HOME reuses LEAVING's glyph mirrored
    // horizontally (most emoji vehicle glyphs, including this tractor,
    // render facing left - flipping it reads as "heading back" instead of
    // "heading out"). Empty string (no class) for everything else.
    public static string IconClass(string activity) => activity == "GOING_HOME" ? "icon-flip" : "";

    public static string StatusClass(string activity, bool connected) => !connected
        ? "status-error"
        : activity switch
        {
            "MOWING" => "status-mowing",
            "CHARGING" or "PARKED_IN_CS" => "status-charging",
            "GOING_HOME" or "LEAVING" => "status-transit",
            _ => "status-idle",
        };

    public static string Label(string activity) => activity switch
    {
        "MOWING" => "Mowing",
        "CHARGING" => "Charging",
        "PARKED_IN_CS" => "Parked",
        "GOING_HOME" => "Going home",
        "LEAVING" => "Leaving",
        "STOPPED_IN_GARDEN" => "Stopped",
        "NOT_APPLICABLE" => "N/A",
        "UNKNOWN" => "Unknown",
        _ => activity,
    };

    // Hover text for an icon - same wording as the "Activity" row's own
    // label, so an icon and its spelled-out equivalent never say different
    // things. Only the connected-aware call sites (the mower card's own
    // header icon) need the `connected` branch; icons in a session
    // list/table represent a historical activity, not live connectivity,
    // so those just use Label(activity) directly.
    public static string Title(string activity, bool connected) => !connected
        ? "Disconnected"
        : Label(activity);

    // Shared by Dashboard's "last 7 days" block and MowerDetails' full daily
    // rollup table, so the two don't drift into slightly different formats.
    public static string MowingCell(List<WorkAreaTime> mowing) => mowing.Count == 0
        ? "—"
        : string.Join(", ", mowing.Select(m => $"{m.Duration.FormatDuration()}{(m.WorkAreaName is null ? "" : $" [{m.WorkAreaName}]")}"));

    public static string DurationCell(TimeSpan span) => span > TimeSpan.Zero ? span.FormatDuration() : "—";

    // Shared by Dashboard (building each MowerCard) and MowerDetails
    // (rendering live MowerAttributes directly) so both resolve "which area
    // is it in" and "when's the next start" identically instead of two
    // independent copies of the same lookup drifting apart.
    public static string? WorkAreaName(MowerAttributes a) => a.Mower.WorkAreaId is { } id
        ? (a.WorkAreas ?? []).FirstOrDefault(w => w.WorkAreaId == id)?.Name.Trim()
        : null;

    public static string NextStartLabel(MowerAttributes a) => a.Planner.NextStartTimestamp > 0
        ? DateTimeOffset.FromUnixTimeMilliseconds(a.Planner.NextStartTimestamp).ToLocalTime().ToString("MMM d, HH:mm")
        : "not scheduled";

    // Scalar parameters rather than MowerAttributes directly - Dashboard's
    // MowerCard is a flat, already-resolved DTO (built once in LoadAsync,
    // decoupled from the live API object), so this needs to work from
    // plain values either way; MowerDetails just extracts the same three
    // fields from its own live `a` each call.
    public static string BatteryLabel(int percent, string activity, long remainingChargingSeconds)
    {
        var icon = percent >= 60 ? "🔋" : "🪫";
        var low = percent < 25 ? " (low)" : "";
        var remaining = activity == "CHARGING" && remainingChargingSeconds > 0
            ? $", {TimeSpan.FromSeconds(remainingChargingSeconds).FormatDuration()} remaining"
            : "";
        return $"{icon} {percent}%{low}{remaining}";
    }

    public static string OverrideLabel(string? action) => action switch
    {
        null or "" or "NOT_ACTIVE" => "None",
        "FORCE_PARK" => "Force park",
        "FORCE_MOW" => "Force mow",
        _ => action,
    };

    // Null (not just "None") when there's nothing to show, so callers can
    // hide the whole row instead of rendering a permanent "Restricted: None".
    public static string? RestrictedLabel(string restrictedReason, int? externalReason)
    {
        if (restrictedReason is "" or "NOT_APPLICABLE")
        {
            return null;
        }
        var externalLabel = ExternalReasons.Describe(externalReason);
        return externalLabel is null ? restrictedReason : $"{restrictedReason} - {externalLabel}";
    }

    public static string HeadlightModeLabel(string? mode) => mode switch
    {
        null or "" => "—",
        "ALWAYS_ON" => "Always on",
        "ALWAYS_OFF" => "Always off",
        "EVENING_ONLY" => "Evening only",
        "EVENING_AND_NIGHT" => "Evening & night",
        _ => mode,
    };

    // For the "Operation" (lifetime statistics) section - plain hours
    // (with thousands separators, e.g. "1,716h"), matching how Husqvarna's
    // own app presents these same lifetime counters, rather than
    // FormatDuration's h/m format (tuned for session/day-scale durations,
    // not a multi-year cumulative total).
    public static string LifetimeDuration(long seconds) => $"{Math.Round(TimeSpan.FromSeconds(seconds).TotalHours):N0}h";

    public static string Distance(long meters) => meters >= 1000 ? $"{meters / 1000.0:F1} km" : $"{meters} m";

    // WMO weather interpretation codes, per Open-Meteo's documented mapping
    // (https://open-meteo.com/en/docs - "WMO Weather interpretation codes").
    // Only the day/night split for clear/mostly-clear actually changes the
    // icon; every other code reads the same regardless of time of day.
    public static string WeatherIcon(WeatherInfo weather) => weather.WeatherCode switch
    {
        0 => weather.IsDay ? "☀️" : "🌙",
        1 or 2 => weather.IsDay ? "🌤️" : "🌙",
        3 => "☁️",
        45 or 48 => "🌫️",
        51 or 53 or 55 or 56 or 57 => "🌦️",
        61 or 63 or 65 or 66 or 67 => "🌧️",
        71 or 73 or 75 or 77 => "❄️",
        80 or 81 or 82 => "🌦️",
        85 or 86 => "🌨️",
        95 or 96 or 99 => "⛈️",
        _ => "🌡️",
    };

    public static string WeatherLabel(int weatherCode) => weatherCode switch
    {
        0 => "Clear sky",
        1 => "Mainly clear",
        2 => "Partly cloudy",
        3 => "Overcast",
        45 or 48 => "Fog",
        51 or 53 or 55 => "Drizzle",
        56 or 57 => "Freezing drizzle",
        61 or 63 or 65 => "Rain",
        66 or 67 => "Freezing rain",
        71 or 73 or 75 or 77 => "Snow",
        80 or 81 or 82 => "Rain showers",
        85 or 86 => "Snow showers",
        95 => "Thunderstorm",
        96 or 99 => "Thunderstorm with hail",
        _ => "Unknown",
    };
}
