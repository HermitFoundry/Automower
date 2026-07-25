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

    public static string HeadlightModeLabel(string? mode) => mode switch
    {
        null or "" => "—",
        "ALWAYS_ON" => "Always on",
        "ALWAYS_OFF" => "Always off",
        "EVENING_ONLY" => "Evening only",
        "EVENING_AND_NIGHT" => "Evening & night",
        _ => mode,
    };

    // For the "Operation" (lifetime statistics) section - FormatDuration
    // (h/m) reads fine for a session or a day, but a lifetime running-time
    // counter is routinely 1000+ hours, where "1716h58m" stops being
    // readable. Falls back to FormatDuration under a day, where it's still
    // the more natural unit.
    public static string LifetimeDuration(long seconds)
    {
        var span = TimeSpan.FromSeconds(seconds);
        return span.TotalDays >= 1 ? $"{(int)span.TotalDays}d {span.Hours}h" : span.FormatDuration();
    }

    public static string Distance(long meters) => meters >= 1000 ? $"{meters / 1000.0:F1} km" : $"{meters} m";
}
