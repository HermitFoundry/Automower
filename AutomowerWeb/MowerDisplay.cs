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

    // Just the total, no per-work-area breakdown/brackets - unlike
    // MowingCell above. Used for the dashboard top section's compact
    // "Mowed" line, where a work area name (e.g. "[oversiden]") pushed
    // that row onto a second line on some cards but not others, throwing
    // off the Charging row/pie chart's vertical alignment across cards.
    // MowingCell itself is unchanged and still used everywhere the
    // per-area breakdown is actually wanted (the daily/monthly rollup
    // tables, the "last 7 days" block).
    public static string MowingTotalCell(List<WorkAreaTime> mowing)
        => DurationCell(mowing.Aggregate(TimeSpan.Zero, (sum, m) => sum + m.Duration));

    // Shared by Dashboard (building each MowerCard) and MowerDetails
    // (rendering live MowerAttributes directly) so both resolve "which area
    // is it in" and "when's the next start" identically instead of two
    // independent copies of the same lookup drifting apart.
    public static string? WorkAreaName(MowerAttributes a) => a.Mower.WorkAreaId is { } id
        ? (a.WorkAreas ?? []).FirstOrDefault(w => w.WorkAreaId == id)?.Name.Trim()
        : null;

    // The full WorkArea for whichever one the mower is currently in - same
    // lookup as WorkAreaName, just returning the record itself instead of
    // just its name, for callers that also need Progress/Type (the
    // dashboard's Progress row).
    public static WorkArea? CurrentWorkArea(MowerAttributes a) => a.Mower.WorkAreaId is { } id
        ? (a.WorkAreas ?? []).FirstOrDefault(w => w.WorkAreaId == id)
        : null;

    // Dashboard top-block "Progress" row: "—" when there's no current work
    // area at all (matches the "Work area" row's own "—" for that case,
    // e.g. parked with no active area), "N/A" + an explanatory hover title
    // when the current area is a "RANDOM"/Irregular pattern (Progress is
    // never present for one - confirmed live, 2026-07-28 - not just zero),
    // otherwise the real percentage. No title text for the other two cases
    // - Razor's title="@..." renders no attribute at all for a null string,
    // so nothing to explain when there's nothing surprising going on.
    public static (string Text, string? Title) DashboardProgressCell(WorkArea? currentWorkArea) => currentWorkArea switch
    {
        null => ("—", null),
        { Progress: { } p } => ($"{p}%", null),
        _ => ("N/A", "Progress not available with Irregular"),
    };

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

    public static string WorkAreaPatternLabel(string type) => type switch
    {
        "RANDOM" => "Random",
        "SYSTEMATIC" => "Systematic",
        "" => "—",
        _ => type,
    };

    // Progress only means anything for a "SYSTEMATIC" (EPOS-guided,
    // precise-coverage) work area - a "RANDOM" one doesn't have a
    // well-defined "% covered" at all, and the raw API omits Progress
    // entirely for one rather than reporting a meaningless value
    // (confirmed live, 2026-07-28).
    public static string WorkAreaProgressCell(WorkArea wa)
        => wa.Progress is { } p ? $"{p}%" : "—";

    // Extra detail for the Progress cell's hover title - empty string for a
    // RANDOM area (nothing meaningful to add), not null, so Razor's
    // title="@..." doesn't render a literal "title" attribute with no value.
    public static string WorkAreaProgressTitle(WorkArea wa)
    {
        if (!string.Equals(wa.Type, "SYSTEMATIC", StringComparison.OrdinalIgnoreCase))
        {
            return "";
        }

        var completed = wa.LastTimeCompleted > 0
            ? DateTimeOffset.FromUnixTimeMilliseconds(wa.LastTimeCompleted).ToLocalTime().ToString("yyyy-MM-dd HH:mm")
            : "never";
        var orientationNote = wa.Orientation is { } o ? $"Stripe orientation: {o}°. " : "";
        return $"{orientationNote}Last completed: {completed}";
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
    // `targetDate` is whichever day the dashboard's Previous/Next control is
    // currently showing - only when it's actually today does "now" clip an
    // in-progress session; a past day's window is drawn in full up to 18:00
    // since nothing about it is still "in progress".
    public static List<PieSlice> BuildDayPieSlices(IReadOnlyList<TrackSession> todaySessions, DateOnly targetDate)
    {
        var now = DateTimeOffset.Now;
        var windowStart = new DateTimeOffset(targetDate.Year, targetDate.Month, targetDate.Day, ChartWindowStartHour, 0, 0, now.Offset);
        var windowEnd = windowStart.AddHours(ChartWindowHours);
        var effectiveNow = targetDate == DateOnly.FromDateTime(now.Date) ? now : windowEnd;

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
            var segEndRaw = s.End ?? effectiveNow;
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
            // Dedicated --chart-* colors, not the app-wide --mowing/--charging
            // tokens used elsewhere (card border accents, dt labels) - those
            // are muted, theme-tuned accents that didn't read clearly
            // against this chart's own grey background (see app.css).
            var colorVar = s.Activity == "MOWING" ? "var(--chart-mowing)" : "var(--chart-charging)";
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

    // Cycled by work area index, not tied to any semantic meaning (unlike
    // --chart-mowing/--chart-charging) - just enough distinct, theme-aware
    // colors to tell a handful of work areas apart. See app.css.
    private static readonly string[] CoverageColorVars =
        ["var(--coverage-1)", "var(--coverage-2)", "var(--coverage-3)", "var(--coverage-4)"];

    public readonly record struct CoverageDot(double Cx, double Cy, string ColorVar);
    public readonly record struct CoverageLegendItem(string Name, string ColorVar, int Count);

    // Precomputed SVG polyline "points" attribute value (e.g. "1.23,4.56
    // 7.89,1.01 ...") - built once here rather than re-joined in the Razor
    // page, same reasoning as CoverageDot carrying already-rounded Cx/Cy.
    public readonly record struct CoverageLine(string Points);

    public record CoveragePlot(
        string ViewBox, List<CoverageDot> Dots, List<CoverageLegendItem> Legend,
        List<CoverageLine> TransportLines, CoverageDot? CurrentPosition);

    // Real GPS fixes recorded while actually mowing (CoverageService already
    // filtered to activity == "MOWING" - see its own comment on why: a poll
    // taken while LEAVING/GOING_HOME still carries a workAreaId, so without
    // that filter transit path points contaminate a work area's own shape),
    // plotted as raw dots - deliberately NOT a computed boundary yet. A
    // first attempt at drawing one (a convex hull, done as a one-off Python
    // script, not in the app) produced boundaries that visibly cut across
    // unrelated areas - the dot density alone already shows the rough shape
    // well enough to look at while that gets sorted out. One shared
    // projection across all of a mower's work areas (and now the transport
    // lines/current position too - see below), so everything's real
    // relative positions/sizes stay comparable on one plot.
    //
    // transportPaths (CoverageService.GetLatestTransportPaths - already just
    // the single most recent LEAVING run and most recent GOING_HOME run, not
    // full history) become black polylines; currentPosition (the mower's
    // live GPS fix, not anything from stored history - the Razor page
    // passes MowerAttributes.Positions[0] directly) becomes a single red dot
    // drawn on top of everything else.
    public static CoveragePlot BuildCoveragePlot(
        List<WorkAreaCoverage> coverage, IReadOnlyDictionary<long, string> workAreaNames,
        List<TransportPath> transportPaths, GpsPoint? currentPosition)
    {
        var withPoints = coverage.Where(c => c.Points.Count > 0).ToList();
        var transportWithPoints = transportPaths.Where(t => t.Points.Count > 0).ToList();
        if (withPoints.Count == 0 && transportWithPoints.Count == 0 && currentPosition is null)
        {
            return new CoveragePlot("0 0 1 1", [], [], [], null);
        }

        // Bounding box has to cover everything that'll be plotted, not just
        // the per-area dots - a transit line often reaches well outside the
        // lawn area itself (e.g. all the way to the charging station), and
        // the live position could be anywhere.
        var allPoints = withPoints.SelectMany(c => c.Points)
            .Concat(transportWithPoints.SelectMany(t => t.Points))
            .Concat(currentPosition is { } cp ? [cp] : [])
            .ToList();
        var lat0 = allPoints.Average(p => p.Lat);
        const double MetersPerDegLat = 111320.0;
        var metersPerDegLon = MetersPerDegLat * Math.Cos(lat0 * Math.PI / 180.0);
        var minLat = allPoints.Min(p => p.Lat);
        var minLon = allPoints.Min(p => p.Lon);

        (double X, double Y) ToLocalMeters(GpsPoint p)
            => ((p.Lon - minLon) * metersPerDegLon, (p.Lat - minLat) * MetersPerDegLat);

        var maxX = allPoints.Max(p => ToLocalMeters(p).X);
        var maxY = allPoints.Max(p => ToLocalMeters(p).Y);
        const double Pad = 3.0;
        var svgHeight = maxY + (2 * Pad);

        (double X, double Y) ToSvg(GpsPoint p)
        {
            var (x, y) = ToLocalMeters(p);
            // SVG y grows downward; north (higher latitude) should be up.
            return (x + Pad, svgHeight - (y + Pad));
        }

        var dots = new List<CoverageDot>();
        var legend = new List<CoverageLegendItem>();
        for (var i = 0; i < withPoints.Count; i++)
        {
            var area = withPoints[i];
            var colorVar = CoverageColorVars[i % CoverageColorVars.Length];
            var name = workAreaNames.TryGetValue(area.WorkAreaId, out var n) ? n : $"area {area.WorkAreaId}";
            legend.Add(new CoverageLegendItem(name, colorVar, area.Points.Count));
            foreach (var p in area.Points)
            {
                var (x, y) = ToSvg(p);
                dots.Add(new CoverageDot(Math.Round(x, 2), Math.Round(y, 2), colorVar));
            }
        }

        var transportLines = transportWithPoints
            .Select(t => new CoverageLine(string.Join(' ', t.Points.Select(p =>
            {
                var (x, y) = ToSvg(p);
                return string.Create(CultureInfo.InvariantCulture, $"{x:F2},{y:F2}");
            }))))
            .ToList();

        CoverageDot? currentPositionDot = null;
        if (currentPosition is { } pos)
        {
            var (x, y) = ToSvg(pos);
            currentPositionDot = new CoverageDot(Math.Round(x, 2), Math.Round(y, 2), "red");
        }

        var viewBox = string.Create(CultureInfo.InvariantCulture,
            $"0 0 {maxX + (2 * Pad):F2} {svgHeight:F2}");
        return new CoveragePlot(viewBox, dots, legend, transportLines, currentPositionDot);
    }
}
