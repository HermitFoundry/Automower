using System.Globalization;
using System.Reflection;
using System.Text.Json;
using AutomowerConsole.Core;

if (args.Length == 0)
{
    PrintUsage();
    return;
}

var command = args[0].ToLowerInvariant();
var rest = args.Skip(1).ToArray();

Config? cachedConfig = null;

// Cheap to construct eagerly - none of these touch AutomowerConnect.Instance
// (and therefore never read config.json) until a method body that actually
// needs the API runs. help/config/errorcodes/current rely on that to keep
// working with no config.json at all.
var mowerService = new MowerService();
var mowerDetailService = new MowerDetailService();
var scheduleService = new ScheduleService();
var trackingService = new TrackingService(scheduleService);

switch (command)
{
    case "list":
        await CommandList();
        break;
    case "use":
        await CommandUse(rest);
        break;
    case "current":
        CommandCurrent();
        break;
    case "status":
        await CommandStatus(rest);
        break;
    case "messages":
        await CommandMessages(rest);
        break;
    case "errorcodes":
        CommandErrorCodes();
        break;
    case "workareas":
        await CommandWorkAreas(rest);
        break;
    case "workarea":
        await CommandWorkArea(rest);
        break;
    case "stayoutzones":
        await CommandStayOutZones(rest);
        break;
    case "track":
        await CommandTrack(rest);
        break;
    case "config":
        CommandConfig(rest);
        break;
    case "schedule":
        await CommandSchedule(rest);
        break;
    case "sessions":
        await CommandSessions(rest);
        break;
    case "daily":
        await CommandDaily(rest);
        break;
    case "monthly":
        await CommandMonthly(rest);
        break;
    case "help":
    case "-h":
    case "--help":
        PrintUsage();
        break;
    default:
        Console.WriteLine($"Unknown command: {command}");
        PrintUsage();
        break;
}

return;

Config GetConfig() => cachedConfig ??= Storage.LoadConfig();

// 'config' bypasses GetConfig()/LoadConfig() (which require AppKey/AppSecret
// to already be set) since it's also how you set them in the first place.
void CommandConfig(string[] configArgs)
{
    var config = Storage.LoadConfigForEditing();
    var properties = typeof(Config).GetProperties(BindingFlags.Public | BindingFlags.Instance);

    if (configArgs.Length == 0)
    {
        Console.WriteLine("Current config.json:");
        foreach (var property in properties)
        {
            var value = property.GetValue(config);
            var display = property.Name is "AppKey" or "AppSecret"
                ? MaskSecret(value as string ?? "")
                : value;
            Console.WriteLine($"  {property.Name} = {display}");
        }
        Console.WriteLine("\nSet values with: automower config Key=Value [Key=Value ...]");
        return;
    }

    foreach (var arg in configArgs)
    {
        var parts = arg.Split('=', 2);
        if (parts.Length != 2)
        {
            Console.WriteLine($"Ignoring '{arg}': expected Key=Value.");
            continue;
        }

        var (key, rawValue) = (parts[0], parts[1]);
        var property = properties.FirstOrDefault(p => string.Equals(p.Name, key, StringComparison.OrdinalIgnoreCase));
        if (property is null)
        {
            Console.WriteLine($"Unknown config key '{key}'. Valid keys: {string.Join(", ", properties.Select(p => p.Name))}");
            continue;
        }

        try
        {
            var value = Convert.ChangeType(rawValue, property.PropertyType, CultureInfo.InvariantCulture);
            property.SetValue(config, value);
            var display = property.Name is "AppKey" or "AppSecret" ? MaskSecret(rawValue) : value;
            Console.WriteLine($"  {property.Name} = {display}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Could not set '{key}' to '{rawValue}': {ex.Message}");
        }
    }

    Storage.SaveConfig(config);
    Console.WriteLine("Saved to config.json.");
}

string MaskSecret(string value)
    => string.IsNullOrEmpty(value) || value is "YOUR_APP_KEY_HERE" or "YOUR_APP_SECRET_HERE"
        ? "(not set)"
        : value.Length <= 8 ? new string('*', value.Length) : $"{value[..4]}...{value[^4..]}";

async Task CommandList()
{
    var mowers = await mowerService.RefreshMowersAsync();

    if (mowers.Count == 0)
    {
        Console.WriteLine("No mowers found on this account.");
        return;
    }

    Console.WriteLine($"Found {mowers.Count} mower(s):");
    for (var i = 0; i < mowers.Count; i++)
    {
        var m = mowers[i];
        Console.WriteLine($"  [{i + 1}] {m.Name} (model: {m.Model}, serial: {m.SerialNumber}, id: {m.Id})");
    }
}

async Task CommandUse(string[] queryArgs)
{
    if (queryArgs.Length == 0)
    {
        Console.WriteLine("Usage: automower use <name|id|list index>");
        return;
    }
    var query = string.Join(" ", queryArgs);

    var match = await mowerService.ResolveExplicitMowerAsync(query);
    if (match is null) return;

    Storage.SaveState(new ActiveState(match.Id, match.Name));
    Console.WriteLine($"Active mower set to: {match.Name} (id: {match.Id})");
}

void CommandCurrent()
{
    var state = Storage.LoadState();
    if (state is null)
    {
        Console.WriteLine("No active mower set. Use 'use <name|id|index>' to set one.");
        return;
    }
    Console.WriteLine($"Active mower: {state.ActiveMowerName} (id: {state.ActiveMowerId})");
}

async Task CommandStatus(string[] statusArgs)
{
    var showAll = statusArgs.Any(a => a is "--all" or "-a");
    var mowerQuery = statusArgs.FirstOrDefault(a => a is not ("--all" or "-a"));

    var resolved = await mowerService.ResolveMowerAsync(mowerQuery);
    if (resolved is null) return;
    var (mowerId, mowerName) = resolved.Value;

    if (showAll)
    {
        var raw = await mowerDetailService.GetMowerRawAsync(mowerId);
        using var doc = JsonDocument.Parse(raw);
        var pretty = JsonSerializer.Serialize(doc.RootElement, new JsonSerializerOptions { WriteIndented = true });
        Console.WriteLine($"Full status for {mowerName}:");
        Console.WriteLine(pretty);
        return;
    }

    var mower = await mowerDetailService.GetMowerDetailAsync(mowerId);
    var a = mower.Attributes;

    var statusTime = DateTimeOffset.FromUnixTimeMilliseconds(a.Metadata.StatusTimestamp).ToLocalTime();
    var nextStart = a.Planner.NextStartTimestamp > 0
        ? DateTimeOffset.FromUnixTimeMilliseconds(a.Planner.NextStartTimestamp).ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss")
        : "not scheduled";

    Console.WriteLine($"Status for {a.System.Name}:");
    Console.WriteLine($"  Connected:  {(a.Metadata.Connected ? "yes" : "no")} (as of {statusTime:yyyy-MM-dd HH:mm:ss})");
    Console.WriteLine($"  State:      {a.Mower.State}");
    Console.WriteLine($"  Activity:   {a.Mower.Activity}");
    Console.WriteLine($"  Mode:       {a.Mower.Mode}");
    if (a.Mower.WorkAreaId is { } currentWorkAreaId)
    {
        var workAreaName = (a.WorkAreas ?? []).FirstOrDefault(wa => wa.WorkAreaId == currentWorkAreaId)?.Name.Trim();
        Console.WriteLine($"  Work area:  {workAreaName ?? currentWorkAreaId.ToString(CultureInfo.InvariantCulture)}");
    }
    Console.WriteLine($"  Battery:    {a.Battery.BatteryPercent}%");
    Console.WriteLine($"  Next start: {nextStart}");
    if (a.Mower.InactiveReason != "NONE")
    {
        Console.WriteLine($"  Inactive reason: {a.Mower.InactiveReason}{InactiveReasonCaveat(a.Mower.InactiveReason)}");
    }
    if (a.Mower.ErrorCode != 0)
    {
        Console.WriteLine($"  Error code: {a.Mower.ErrorCode} ({ErrorCodes.Describe(a.Mower.ErrorCode)})");
    }
}

// The API reports "SEARCHING_FOR_SATELLITES" as a catch-all inactive reason -
// it does not reliably mean the mower is waiting on GPS. It has also been
// observed for lost WiFi/4G connectivity and for charging station problems.
string InactiveReasonCaveat(string inactiveReason)
    => inactiveReason == "SEARCHING_FOR_SATELLITES"
        ? " (ambiguous - can also mean lost WiFi/4G connectivity or a charging station problem)"
        : "";

async Task CommandMessages(string[] messagesArgs)
{
    var resolved = await mowerService.ResolveMowerAsync(messagesArgs.FirstOrDefault());
    if (resolved is null) return;
    var (mowerId, mowerName) = resolved.Value;

    var messages = await mowerDetailService.GetMessagesAsync(mowerId);

    if (messages.Length == 0)
    {
        Console.WriteLine($"No messages for {mowerName}.");
        return;
    }

    Console.WriteLine($"Messages for {mowerName}:");
    foreach (var msg in messages.OrderByDescending(m => m.Time))
    {
        var time = DateTimeOffset.FromUnixTimeSeconds(msg.Time).ToLocalTime();
        var location = msg.Latitude is not null && msg.Longitude is not null
            ? $" @ ({msg.Latitude.Value.ToString(CultureInfo.InvariantCulture)}, {msg.Longitude.Value.ToString(CultureInfo.InvariantCulture)})"
            : "";
        Console.WriteLine($"  [{msg.Severity,-7}] {time:yyyy-MM-dd HH:mm:ss} - {ErrorCodes.Describe(msg.Code)} (code {msg.Code}){location}");
    }
}

void CommandErrorCodes()
{
    Console.WriteLine("Automower error codes:");
    foreach (var (code, text) in ErrorCodes.Descriptions.OrderBy(kv => kv.Key))
    {
        Console.WriteLine($"  {code,4}  {text}");
    }
}

async Task CommandWorkAreas(string[] workAreasArgs)
{
    var resolved = await mowerService.ResolveMowerAsync(workAreasArgs.FirstOrDefault());
    if (resolved is null) return;
    var (mowerId, mowerName) = resolved.Value;

    var mower = await mowerDetailService.GetMowerDetailAsync(mowerId);
    var areas = mower.Attributes.WorkAreas;

    if (areas is null || areas.Length == 0)
    {
        Console.WriteLine($"No work areas configured for {mowerName}.");
        return;
    }

    Console.WriteLine($"Work areas for {mowerName}:");
    foreach (var wa in areas)
    {
        var abandoned = wa.LastTimeAbandoned > 0
            ? DateTimeOffset.FromUnixTimeMilliseconds(wa.LastTimeAbandoned).ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss")
            : "never";

        Console.WriteLine($"  [{wa.WorkAreaId}] {wa.Name.Trim()}");
        Console.WriteLine($"      Type:                  {wa.Type}");
        Console.WriteLine($"      Enabled:               {wa.Enabled}");
        Console.WriteLine($"      Cutting height:        {wa.CuttingHeight}%");
        Console.WriteLine($"      Use global cut height: {wa.UseGlobalCuttingHeight}");
        Console.WriteLine($"      Last time abandoned:   {abandoned}");
    }
}

async Task CommandWorkArea(string[] queryArgs)
{
    if (queryArgs.Length == 0)
    {
        Console.WriteLine("Usage: automower workarea <name|id> [mower]");
        return;
    }
    var query = queryArgs[0];
    var mowerQuery = queryArgs.Length > 1 ? string.Join(" ", queryArgs.Skip(1)) : null;

    var resolved = await mowerService.ResolveMowerAsync(mowerQuery);
    if (resolved is null) return;
    var (mowerId, mowerName) = resolved.Value;

    var mower = await mowerDetailService.GetMowerDetailAsync(mowerId);
    var areas = mower.Attributes.WorkAreas;

    if (areas is null || areas.Length == 0)
    {
        Console.WriteLine($"No work areas configured for {mowerName}.");
        return;
    }

    WorkArea? match = null;

    if (long.TryParse(query, out var workAreaId))
    {
        match = areas.FirstOrDefault(a => a.WorkAreaId == workAreaId);
    }

    match ??= areas.FirstOrDefault(a => string.Equals(a.Name.Trim(), query, StringComparison.OrdinalIgnoreCase));

    if (match is null)
    {
        var candidates = areas.Where(a => a.Name.Contains(query, StringComparison.OrdinalIgnoreCase)).ToList();
        if (candidates.Count == 1)
        {
            match = candidates[0];
        }
        else if (candidates.Count > 1)
        {
            Console.WriteLine($"Multiple work areas match '{query}':");
            foreach (var c in candidates)
            {
                Console.WriteLine($"  [{c.WorkAreaId}] {c.Name.Trim()}");
            }
            return;
        }
    }

    if (match is null)
    {
        Console.WriteLine($"No work area found matching '{query}'. Run 'workareas' to see available work areas.");
        return;
    }

    var detail = await mowerDetailService.GetWorkAreaDetailAsync(mowerId, match.WorkAreaId);

    var abandoned = detail.LastTimeAbandoned > 0
        ? DateTimeOffset.FromUnixTimeMilliseconds(detail.LastTimeAbandoned).ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss")
        : "never";

    Console.WriteLine($"Work area [{detail.WorkAreaId}] {detail.Name.Trim()} on {mowerName}:");
    Console.WriteLine($"  Type:                  {detail.Type}");
    Console.WriteLine($"  Enabled:               {detail.Enabled}");
    Console.WriteLine($"  Cutting height:        {detail.CuttingHeight}%");
    Console.WriteLine($"  Use global cut height: {detail.UseGlobalCuttingHeight}");
    Console.WriteLine($"  Last time abandoned:   {abandoned}");

    var tasks = detail.Calendar?.Tasks ?? [];
    if (tasks.Length == 0)
    {
        Console.WriteLine("  Schedule: none");
    }
    else
    {
        Console.WriteLine("  Schedule:");
        foreach (var t in tasks)
        {
            Console.WriteLine($"    {FormatCalendarTask(t)}");
        }
    }
}

string FormatCalendarTask(CalendarTask t)
{
    var days = new List<string>();
    if (t.Monday) days.Add("Mon");
    if (t.Tuesday) days.Add("Tue");
    if (t.Wednesday) days.Add("Wed");
    if (t.Thursday) days.Add("Thu");
    if (t.Friday) days.Add("Fri");
    if (t.Saturday) days.Add("Sat");
    if (t.Sunday) days.Add("Sun");

    var start = TimeSpan.FromMinutes(t.Start);
    var end = TimeSpan.FromMinutes(t.Start + t.Duration);
    var dayList = days.Count > 0 ? string.Join(",", days) : "none";

    return $"{dayList,-20} {start:hh\\:mm}-{end:hh\\:mm} ({t.Duration} min)";
}

async Task CommandStayOutZones(string[] stayOutZonesArgs)
{
    var resolved = await mowerService.ResolveMowerAsync(stayOutZonesArgs.FirstOrDefault());
    if (resolved is null) return;
    var (mowerId, mowerName) = resolved.Value;

    var mower = await mowerDetailService.GetMowerDetailAsync(mowerId);
    var zones = mower.Attributes.StayOutZones;

    if (zones is null || zones.Zones.Length == 0)
    {
        Console.WriteLine($"No stay-out zones configured for {mowerName}.");
        return;
    }

    Console.WriteLine($"Stay-out zones for {mowerName} (dirty: {zones.Dirty}):");
    foreach (var z in zones.Zones)
    {
        Console.WriteLine($"  [{z.Id}] {z.Name} - {(z.Enabled ? "enabled" : "disabled")}");
    }
}

async Task CommandTrack(string[] trackArgs)
{
    var intervalArg = trackArgs.FirstOrDefault(a => int.TryParse(a, out _));
    var mowerQuery = trackArgs.FirstOrDefault(a => a != intervalArg);

    var resolved = await mowerService.ResolveMowerAsync(mowerQuery);
    if (resolved is null) return;
    var (mowerId, mowerName) = resolved.Value;

    var config = GetConfig();
    var activeIntervalSeconds = intervalArg is not null
        ? int.Parse(intervalArg, CultureInfo.InvariantCulture)
        : config.ScheduledIntervalSeconds;

    using var cts = new CancellationTokenSource();
    Console.CancelKeyPress += (_, e) =>
    {
        e.Cancel = true;
        cts.Cancel();
    };

    await trackingService.RunAsync(mowerId, mowerName, config, activeIntervalSeconds, cts.Token);
}

async Task CommandSchedule(string[] scheduleArgs)
{
    var resolved = await mowerService.ResolveMowerAsync(scheduleArgs.FirstOrDefault());
    if (resolved is null) return;
    var (mowerId, mowerName) = resolved.Value;

    var mower = await mowerDetailService.GetMowerDetailAsync(mowerId);
    var tasks = mower.Attributes.Calendar?.Tasks ?? [];

    scheduleService.SaveScheduleForMower(mowerId, mowerName, tasks);

    if (tasks.Length == 0)
    {
        Console.WriteLine($"No schedule configured for {mowerName}. Cached to schedule.json (empty).");
        return;
    }

    var workAreaNames = (mower.Attributes.WorkAreas ?? [])
        .Where(wa => !string.IsNullOrWhiteSpace(wa.Name))
        .ToDictionary(wa => wa.WorkAreaId, wa => wa.Name.Trim());

    Console.WriteLine($"Schedule for {mowerName} (refreshed in schedule.json):");
    foreach (var t in tasks)
    {
        var workAreaNote = t.WorkAreaId is { } waId
            ? $" [{(workAreaNames.TryGetValue(waId, out var waName) ? waName : waId.ToString(CultureInfo.InvariantCulture))}]"
            : "";
        Console.WriteLine($"  {FormatCalendarTask(t)}{workAreaNote}");
    }

    var nextCalendar = scheduleService.NextCalendarStart(tasks, DateTimeOffset.Now);
    var nextCalendarLabel = nextCalendar is null ? "none" : nextCalendar.Value.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
    var nextPlanned = mower.Attributes.Planner.NextStartTimestamp > 0
        ? DateTimeOffset.FromUnixTimeMilliseconds(mower.Attributes.Planner.NextStartTimestamp).ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)
        : "not scheduled";

    Console.WriteLine();
    Console.WriteLine($"  Next calendar start: {nextCalendarLabel} (from the recurring schedule above)");
    Console.WriteLine($"  Next planned start:  {nextPlanned} (the mower's live decision - may differ, e.g. due to battery)");
    if (mower.Attributes.Planner.RestrictedReason is not ("NOT_APPLICABLE" or ""))
    {
        Console.WriteLine($"  Restricted: {mower.Attributes.Planner.RestrictedReason}");
    }
}

// Reads a mower's track-<mower>.jsonl and summarizes it into sessions: runs
// of consecutive polls sharing the same (mower.activity, mower.workAreaId) -
// split on either changing, since activity can stay MOWING across a switch
// from one work area straight into the next. A session's end is the
// timestamp of the *next* differing poll (not its own last poll), since
// that's the earliest point we can confirm the state changed - this matters
// most for charger stays, where 'track' only logs one poll on arrival and
// then skips repeats, so a session can be a single log line whose real end
// is only knowable from what comes after it.
async Task CommandSessions(string[] sessionArgs)
{
    var showCalendar = sessionArgs.Any(a => a is "--calendar" or "-c");
    var mowerQuery = sessionArgs.FirstOrDefault(a => a is not ("--calendar" or "-c"));

    var resolved = await mowerService.ResolveMowerAsync(mowerQuery);
    if (resolved is null) return;
    var (_, mowerName) = resolved.Value;

    var sessions = trackingService.SummarizeSessions(mowerName, showCalendar);
    if (sessions.Count == 0) return;

    Console.WriteLine($"Sessions for {mowerName} (newest first, from {Storage.GetTrackLogPath(mowerName)}):");
    foreach (var s in sessions)
    {
        var endLabel = s.End is null ? "ongoing" : s.End.Value.ToString("HH:mm", CultureInfo.InvariantCulture);
        var duration = (s.End ?? DateTimeOffset.Now) - s.Start;

        var durationLabel = $"({duration.FormatDuration()})";
        var workAreaLabel = s.WorkAreaName is not null ? $"  [{s.WorkAreaName}]" : "";
        var sessionLine =
            $"  {s.Start:yyyy-MM-dd}  {DescribeActivity(s.Activity),-11} {s.Start:HH:mm}-{endLabel,-7} " +
            $"{durationLabel,-9}  battery {s.BatteryStart,3}% -> {s.BatteryEnd,3}%{workAreaLabel}";

        // No marker at all when ChargeCompleteAt is null and the session has
        // already ended - could mean it left before reaching 100%, or could
        // just be an old log line from before this was tracked (see
        // TrackSession.ChargeCompleteAt) - not distinguishable, so don't
        // assert either.
        if (TrackingService.IsAtCharger(s.Activity))
        {
            if (s.ChargeCompleteAt is { } completeAt)
            {
                sessionLine += $"  full at {completeAt:HH:mm}";
            }
            else if (s.End is null)
            {
                sessionLine += "  still charging";
            }
        }

        if (showCalendar && TrackingService.IsAtCharger(s.Activity))
        {
            var nextCalendarLabel = s.NextCalendarStart is null ? "none" : s.NextCalendarStart.Value.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
            var nextPlannedLabel = s.NextPlannedStart is null ? "not scheduled" : s.NextPlannedStart.Value.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
            sessionLine += $"  next calendar start: {nextCalendarLabel}   next planned start: {nextPlannedLabel}";
        }

        Console.WriteLine(sessionLine);
    }
}

// One line per calendar day: total Mowing time per work area that day
// (repeated on the line for each additional area, summed if the same area
// was mowed more than once), then Charging and (if any) Parked last -
// neither is tied to a work area, so both are outside that list, not part
// of it. See TrackingService.AggregateDailyActivity for the day-attribution,
// CHARGING/PARKED_IN_CS-combining, and Charging-vs-Parked split rules.
async Task CommandDaily(string[] dailyArgs)
{
    var resolved = await mowerService.ResolveMowerAsync(dailyArgs.FirstOrDefault());
    if (resolved is null) return;
    var (_, mowerName) = resolved.Value;

    var days = trackingService.SummarizeDailyActivity(mowerName);
    if (days.Count == 0) return;

    Console.WriteLine($"Daily activity for {mowerName} (newest first, from {Storage.GetTrackLogPath(mowerName)}):");
    foreach (var day in days)
    {
        var parts = day.Mowing.Select(m => m.WorkAreaName is null
            ? $"Mowing {m.Duration.FormatDuration()}"
            : $"Mowing {m.Duration.FormatDuration()} [{m.WorkAreaName}]").ToList();

        if (day.Charging > TimeSpan.Zero)
        {
            parts.Add($"Charging {day.Charging.FormatDuration()}");
        }
        if (day.Parked > TimeSpan.Zero)
        {
            parts.Add($"Parked {day.Parked.FormatDuration()}");
        }

        var date = day.Date.ToDateTime(TimeOnly.MinValue).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        Console.WriteLine($"  {date}  {string.Join("   ", parts)}");
    }
}

async Task CommandMonthly(string[] monthlyArgs)
{
    var resolved = await mowerService.ResolveMowerAsync(monthlyArgs.FirstOrDefault());
    if (resolved is null) return;
    var (_, mowerName) = resolved.Value;

    var months = trackingService.SummarizeMonthlyActivity(mowerName);
    if (months.Count == 0) return;

    Console.WriteLine($"Monthly activity for {mowerName} (newest first, from {Storage.GetTrackLogPath(mowerName)}):");
    foreach (var month in months)
    {
        var parts = month.Mowing.Select(m => m.WorkAreaName is null
            ? $"Mowing {m.Duration.FormatDuration()}"
            : $"Mowing {m.Duration.FormatDuration()} [{m.WorkAreaName}]").ToList();

        if (month.Charging > TimeSpan.Zero)
        {
            parts.Add($"Charging {month.Charging.FormatDuration()}");
        }
        if (month.Parked > TimeSpan.Zero)
        {
            parts.Add($"Parked {month.Parked.FormatDuration()}");
        }

        var label = month.Month.ToDateTime(TimeOnly.MinValue).ToString("yyyy-MM", CultureInfo.InvariantCulture);
        Console.WriteLine($"  {label}  {string.Join("   ", parts)}");
    }
}

string DescribeActivity(string activity) => activity switch
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

void PrintUsage()
{
    Console.WriteLine("""
        Automower Console - Husqvarna Automower Connect API client

        Commands that act on "the active mower" accept an optional trailing
        [mower] argument (name, id, or list index) to target a different
        mower for just that one call, without changing the active selection.

        Usage:
          automower config                      Show current config.json (secrets masked)
          automower config Key=Value ...        Set one or more config.json values, e.g.
                                                 automower config AppKey=xxx AppSecret=yyy
          automower list                        Fetch and list all mowers, save to mowers.json
          automower use <name|id|index>         Set the active mower (stored in state.json)
          automower current                     Show the currently active mower
          automower status [--all] [mower]      Show current status (optionally for a specific mower)
          automower messages [mower]            Show messages (optionally for a specific mower)
          automower errorcodes                  Show the full table of error codes and their meaning
          automower workareas [mower]           Show all work areas (optionally for a specific mower)
          automower workarea <name|id> [mower]  Show detail for one work area (optionally for a specific mower)
          automower stayoutzones [mower]        Show stay-out zones (optionally for a specific mower)
          automower schedule [mower]            Show the calendar, refresh it in schedule.json, and show
                                                 the live next calendar/planned start
          automower track [seconds] [mower]     Poll status adaptively and log to a per-mower
                                                 track-<mower>.jsonl: fast (default 60s, [seconds]
                                                 overrides) while active or in a scheduled window, else
                                                 every 5 min in daytime, else every 30 min at night
                                                 (22:00-08:00) - all configurable via 'config'. Every
                                                 poll is logged, including while parked at the charger.
          automower sessions [--calendar] [mower]
                                                 Summarize track-<mower>.jsonl into one line per
                                                 mowing/charging/etc. session (split on activity or
                                                 work area changing): date, start-end time, duration,
                                                 battery start% -> end%, and work area name. Charger
                                                 sessions also show "full at HH:mm" (or "still charging").
                                                 --calendar adds the next calendar/planned start (as of
                                                 that poll) under each Charging/Parked session
          automower daily [mower]                One line per calendar day: total Mowing time per work
                                                 area that day (repeated per area), then Charging time
                                                 and, if any, Parked (charged but not mowing) time
          automower monthly [mower]              Same as 'daily', one line per calendar month instead
          automower help                        Show this help
        """);
}
