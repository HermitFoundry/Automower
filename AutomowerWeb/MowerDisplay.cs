using System.Globalization;
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

    // A single colored wedge in the "today at a glance" clock-face chart -
    // PathData is a ready-to-render SVG <path d="..."> value.
    public readonly record struct PieSlice(string PathData, string ColorVar, string Title);

    // The chart's whole circle spans this fixed 12-hour window (not
    // per-mower/dynamic) - simplest option that comfortably covers this
    // account's real calendar schedules (observed 09:00-17:00 and similar),
    // with slack on both ends. Deliberately approximate, per the user
    // ("doesn't have to be superaccurate either, give it some slack") -
    // sessions starting/ending outside the window are just clipped to it
    // rather than resized to fit.
    private const int ChartWindowStartHour = 6;
    private const int ChartWindowHours = 12;

    // Today's activity as clock-face pie wedges, oriented like a real analog
    // clock - 12 (noon, the only "12" our 06:00-18:00 window ever reaches)
    // at the top, 3pm at the right, 6 (am and pm - both ends of the window)
    // at the bottom, 9am at the left - not "elapsed time since the window
    // started" (that put 06:00 at the top instead of 6's actual clock
    // position, confirmed wrong against a real screenshot). Only Mowing
    // (green) and actually-Charging (blue) get their own wedge; everything
    // else - parked, brief transitional activities, and any time with no
    // data at all (before the first session, or not yet elapsed) - is left
    // as the grey background circle showing through (see day-chart-bg in
    // app.css), rather than drawing separate grey wedges over it. Expects
    // `todaySessions` already run through TrackingService.SplitChargerSessions,
    // same list the "Today" session list and the Mowed/Charging totals both
    // use - so the chart, the list, and the totals can never visually
    // disagree with each other.
    public static List<PieSlice> BuildDayPieSlices(IReadOnlyList<TrackSession> todaySessions)
    {
        var now = DateTimeOffset.Now;
        var windowStart = new DateTimeOffset(now.Year, now.Month, now.Day, ChartWindowStartHour, 0, 0, now.Offset);
        var windowEnd = windowStart.AddHours(ChartWindowHours);

        // Position on the dial for a given moment, independent of the
        // window - literally where a real clock's hour hand would point
        // (mod 12, so 6am and 6pm land on the same "6" spot at the bottom).
        static double ClockAngle(DateTimeOffset t) => ((t.Hour % 12) + (t.Minute / 60.0)) / 12.0 * 360.0;

        var slices = new List<PieSlice>();
        foreach (var s in todaySessions)
        {
            if (s.Activity is not ("MOWING" or "CHARGING"))
            {
                continue;
            }

            var segStart = s.Start < windowStart ? windowStart : s.Start;
            var segEndRaw = s.End ?? now;
            var segEnd = segEndRaw > windowEnd ? windowEnd : segEndRaw;
            if (segEnd <= segStart)
            {
                continue;
            }

            var startAngle = ClockAngle(segStart);
            var endAngleRaw = ClockAngle(segEnd);
            // The window spans exactly one full 12-hour lap (06:00 and
            // 18:00 share the same raw clock angle), so the only point a
            // segment can cross is noon (the dial's own wrap point) -
            // whenever that leaves the raw end angle behind the start
            // angle, it means the segment crossed noon and needs a full
            // lap added to keep sweeping forward instead of backward.
            var endAngle = endAngleRaw < startAngle ? endAngleRaw + 360 : endAngleRaw;
            var colorVar = s.Activity == "MOWING" ? "var(--mowing)" : "var(--charging)";
            var title = $"{Label(s.Activity)} {segStart:HH:mm}–{segEnd:HH:mm}";
            slices.Add(new PieSlice(PieSlicePath(startAngle, endAngle), colorVar, title));
        }
        return slices;
    }

    // cx/cy/r match the SVG's own viewBox="0 0 100 100" in Dashboard.razor -
    // 0deg is straight up (12 o'clock), increasing clockwise, matching how
    // BuildDayPieSlices measures elapsed time from the window start.
    private static string PieSlicePath(double startAngleDeg, double endAngleDeg)
    {
        const double cx = 50, cy = 50, r = 45;
        static (double X, double Y) PointOnCircle(double angleDeg)
        {
            var rad = angleDeg * Math.PI / 180.0;
            return (cx + (r * Math.Sin(rad)), cy - (r * Math.Cos(rad)));
        }

        var (x1, y1) = PointOnCircle(startAngleDeg);
        var (x2, y2) = PointOnCircle(endAngleDeg);
        var largeArcFlag = endAngleDeg - startAngleDeg > 180 ? 1 : 0;
        // Explicit InvariantCulture, not relying on AutomowerWeb.csproj's
        // InvariantGlobalization setting to make this safe by coincidence -
        // an SVG path's numbers must use '.', never a locale decimal comma.
        string F(double n) => n.ToString("F2", CultureInfo.InvariantCulture);
        return $"M {cx},{cy} L {F(x1)},{F(y1)} A {r},{r} 0 {largeArcFlag} 1 {F(x2)},{F(y2)} Z";
    }
}
