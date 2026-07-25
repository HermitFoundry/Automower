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
            "GOING_HOME" => "🏡",
            "LEAVING" => "🚜",
            "STOPPED_IN_GARDEN" => "⏸️",
            _ => "❔",
        };

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

    // Shared by Dashboard's "last 7 days" block and MowerDetails' full daily
    // rollup table, so the two don't drift into slightly different formats.
    public static string MowingCell(List<WorkAreaTime> mowing) => mowing.Count == 0
        ? "—"
        : string.Join(", ", mowing.Select(m => $"{m.Duration.FormatDuration()}{(m.WorkAreaName is null ? "" : $" [{m.WorkAreaName}]")}"));

    public static string DurationCell(TimeSpan span) => span > TimeSpan.Zero ? span.FormatDuration() : "—";
}
