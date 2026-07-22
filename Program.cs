using System.Globalization;
using System.Reflection;
using System.Text;
using System.Text.Json;

if (args.Length == 0)
{
    PrintUsage();
    return;
}

var command = args[0].ToLowerInvariant();
var rest = args.Skip(1).ToArray();

HusqvarnaClient? client = null;
Config? cachedConfig = null;

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

HusqvarnaClient GetClient()
{
    if (client is null)
    {
        var config = GetConfig();
        client = new HusqvarnaClient(new HttpClient(), config.AppKey, config.AppSecret);
    }
    return client;
}

void SaveScheduleForMower(string mowerId, string mowerName, CalendarTask[] tasks)
{
    var schedules = Storage.LoadSchedules();
    schedules[mowerId] = new MowerSchedule(mowerName, DateTimeOffset.Now, tasks);
    Storage.SaveSchedules(schedules);
}

CalendarTask[] GetCachedTasks(string mowerId)
    => Storage.LoadSchedules().TryGetValue(mowerId, out var entry) ? entry.Tasks : [];

bool DayFlag(CalendarTask t, DayOfWeek day) => day switch
{
    DayOfWeek.Monday => t.Monday,
    DayOfWeek.Tuesday => t.Tuesday,
    DayOfWeek.Wednesday => t.Wednesday,
    DayOfWeek.Thursday => t.Thursday,
    DayOfWeek.Friday => t.Friday,
    DayOfWeek.Saturday => t.Saturday,
    DayOfWeek.Sunday => t.Sunday,
    _ => false,
};

// True if 'now' falls inside any calendar task's active window, including a
// task from yesterday whose duration wraps past midnight into today.
bool IsWithinSchedule(CalendarTask[] tasks, DateTimeOffset now)
{
    var minuteOfDay = now.Hour * 60 + now.Minute;
    var yesterday = (DayOfWeek)(((int)now.DayOfWeek + 6) % 7);

    foreach (var t in tasks)
    {
        if (DayFlag(t, now.DayOfWeek) && minuteOfDay >= t.Start && minuteOfDay < t.Start + t.Duration)
        {
            return true;
        }

        var wrapMinutes = t.Start + t.Duration - 1440;
        if (wrapMinutes > 0 && DayFlag(t, yesterday) && minuteOfDay < wrapMinutes)
        {
            return true;
        }
    }
    return false;
}

// Earliest calendar task start strictly after 'after', scanning up to 8 days
// forward (guarantees covering a full week even from late in the day today).
// Returns null if there are no tasks at all.
DateTimeOffset? NextCalendarStart(CalendarTask[] tasks, DateTimeOffset after)
{
    if (tasks.Length == 0) return null;

    for (var dayOffset = 0; dayOffset <= 7; dayOffset++)
    {
        var day = after.Date.AddDays(dayOffset);
        var dayOfWeek = day.DayOfWeek;
        foreach (var t in tasks.OrderBy(t => t.Start))
        {
            if (!DayFlag(t, dayOfWeek)) continue;
            var candidate = new DateTimeOffset(day, after.Offset).AddMinutes(t.Start);
            if (candidate > after) return candidate;
        }
    }
    return null;
}

// Nighttime window wraps past midnight when startHour > endHour (e.g. 22 -> 8).
bool IsNighttime(DateTimeOffset now, int startHour, int endHour)
    => startHour > endHour
        ? now.Hour >= startHour || now.Hour < endHour
        : now.Hour >= startHour && now.Hour < endHour;

async Task<List<StoredMower>> EnsureMowersAsync()
{
    var mowers = Storage.LoadMowers();
    if (mowers is null || mowers.Count == 0)
    {
        Console.WriteLine("No cached mower list found, fetching from API...");
        var api = GetClient();
        await api.AuthenticateAsync();
        var fetched = await api.GetMowersAsync();
        mowers = fetched
            .Select(m => new StoredMower(m.Id, m.Attributes.System.Name, m.Attributes.System.Model, m.Attributes.System.SerialNumber))
            .ToList();
        Storage.SaveMowers(mowers);
    }
    return mowers;
}

// Matches by 1-based list index, exact id, exact name, or a unique
// name-contains match. Returns candidates when the query is ambiguous.
(StoredMower? Match, List<StoredMower> Candidates) FindMower(List<StoredMower> mowers, string query)
{
    if (int.TryParse(query, out var index) && index >= 1 && index <= mowers.Count)
    {
        return (mowers[index - 1], []);
    }

    var exact = mowers.FirstOrDefault(m => string.Equals(m.Id, query, StringComparison.OrdinalIgnoreCase))
        ?? mowers.FirstOrDefault(m => string.Equals(m.Name, query, StringComparison.OrdinalIgnoreCase));
    if (exact is not null)
    {
        return (exact, []);
    }

    var candidates = mowers.Where(m => m.Name.Contains(query, StringComparison.OrdinalIgnoreCase)).ToList();
    return candidates.Count == 1 ? (candidates[0], []) : (null, candidates);
}

// Resolves the mower to operate on: an explicit override query (name/id/index)
// if given, otherwise the active mower from state.json. Prints its own error
// and returns null when nothing can be resolved.
async Task<(string Id, string Name)?> ResolveMowerAsync(string? overrideQuery)
{
    if (!string.IsNullOrWhiteSpace(overrideQuery))
    {
        var mowers = await EnsureMowersAsync();
        var (match, candidates) = FindMower(mowers, overrideQuery);

        if (match is not null)
        {
            return (match.Id, match.Name);
        }

        if (candidates.Count > 1)
        {
            Console.WriteLine($"Multiple mowers match '{overrideQuery}':");
            foreach (var c in candidates)
            {
                Console.WriteLine($"  - {c.Name} (id: {c.Id})");
            }
        }
        else
        {
            Console.WriteLine($"No mower found matching '{overrideQuery}'. Run 'list' to see available mowers.");
        }
        return null;
    }

    var state = Storage.LoadState();
    if (state is null)
    {
        Console.WriteLine("No active mower set, and none specified. Use 'use <name|id|index>' first, or pass a mower name/id as an argument.");
        return null;
    }
    return (state.ActiveMowerId, state.ActiveMowerName);
}

async Task CommandList()
{
    var api = GetClient();
    await api.AuthenticateAsync();
    var mowers = await api.GetMowersAsync();

    if (mowers.Length == 0)
    {
        Console.WriteLine("No mowers found on this account.");
        return;
    }

    var stored = mowers
        .Select(m => new StoredMower(m.Id, m.Attributes.System.Name, m.Attributes.System.Model, m.Attributes.System.SerialNumber))
        .ToList();
    Storage.SaveMowers(stored);

    Console.WriteLine($"Found {stored.Count} mower(s):");
    for (var i = 0; i < stored.Count; i++)
    {
        var m = stored[i];
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

    var mowers = await EnsureMowersAsync();
    var (match, candidates) = FindMower(mowers, query);

    if (match is null)
    {
        if (candidates.Count > 1)
        {
            Console.WriteLine($"Multiple mowers match '{query}':");
            foreach (var c in candidates)
            {
                Console.WriteLine($"  - {c.Name} (id: {c.Id})");
            }
        }
        else
        {
            Console.WriteLine($"No mower found matching '{query}'. Run 'list' to see available mowers.");
        }
        return;
    }

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

    var resolved = await ResolveMowerAsync(mowerQuery);
    if (resolved is null) return;
    var (mowerId, mowerName) = resolved.Value;

    var api = GetClient();
    await api.AuthenticateAsync();

    if (showAll)
    {
        var raw = await api.GetMowerRawAsync(mowerId);
        using var doc = JsonDocument.Parse(raw);
        var pretty = JsonSerializer.Serialize(doc.RootElement, new JsonSerializerOptions { WriteIndented = true });
        Console.WriteLine($"Full status for {mowerName}:");
        Console.WriteLine(pretty);
        return;
    }

    var mower = await api.GetMowerAsync(mowerId);
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
    var resolved = await ResolveMowerAsync(messagesArgs.FirstOrDefault());
    if (resolved is null) return;
    var (mowerId, mowerName) = resolved.Value;

    var api = GetClient();
    await api.AuthenticateAsync();
    var messages = await api.GetMessagesAsync(mowerId);

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
    var resolved = await ResolveMowerAsync(workAreasArgs.FirstOrDefault());
    if (resolved is null) return;
    var (mowerId, mowerName) = resolved.Value;

    var api = GetClient();
    await api.AuthenticateAsync();
    var mower = await api.GetMowerAsync(mowerId);
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

    var resolved = await ResolveMowerAsync(mowerQuery);
    if (resolved is null) return;
    var (mowerId, mowerName) = resolved.Value;

    var api = GetClient();
    await api.AuthenticateAsync();
    var mower = await api.GetMowerAsync(mowerId);
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

    var detail = await api.GetWorkAreaAsync(mowerId, match.WorkAreaId);

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
    var resolved = await ResolveMowerAsync(stayOutZonesArgs.FirstOrDefault());
    if (resolved is null) return;
    var (mowerId, mowerName) = resolved.Value;

    var api = GetClient();
    await api.AuthenticateAsync();
    var mower = await api.GetMowerAsync(mowerId);
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

// Activity values that mean "sitting at the charging station", as opposed to
// actually out in the garden. Used to suppress repeat polls while parked.
bool IsAtCharger(string activity) => activity is "CHARGING" or "PARKED_IN_CS";

async Task CommandTrack(string[] trackArgs)
{
    var intervalArg = trackArgs.FirstOrDefault(a => int.TryParse(a, out _));
    var mowerQuery = trackArgs.FirstOrDefault(a => a != intervalArg);

    var resolved = await ResolveMowerAsync(mowerQuery);
    if (resolved is null) return;
    var (mowerId, mowerName) = resolved.Value;

    var config = GetConfig();
    var activeIntervalSeconds = intervalArg is not null
        ? int.Parse(intervalArg, CultureInfo.InvariantCulture)
        : config.ScheduledIntervalSeconds;

    Storage.EnsureDataDir();
    var logPath = Storage.GetTrackLogPath(mowerName);
    var api = GetClient();
    await api.AuthenticateAsync();

    Console.WriteLine($"Tracking {mowerName}. Logging to {logPath}. Press Ctrl+C to stop.");
    Console.WriteLine($"  Active/scheduled: every {activeIntervalSeconds}s   Idle (daytime): every {config.IdleIntervalSeconds}s   " +
                       $"Night ({config.NightStartHour:00}:00-{config.NightEndHour:00}:00): every {config.NightIntervalSeconds}s");
    Console.WriteLine("While parked at the charging station and no schedule/mowing is active, only the first poll after arrival is logged.");
    Console.WriteLine("The mower's schedule is refreshed from schedule.json's cache each poll (no extra API cost) - run 'schedule' to force an update.");

    using var cts = new CancellationTokenSource();
    Console.CancelKeyPress += (_, e) =>
    {
        e.Cancel = true;
        cts.Cancel();
    };

    // Seed with whatever's cached so the very first wait (before any poll)
    // already has something to work with; refreshed for real after each poll.
    var tasks = GetCachedTasks(mowerId);

    string? lastActivity = null;
    var recordCount = 0;
    long totalBytes = 0;

    while (!cts.IsCancellationRequested)
    {
        var timestamp = DateTimeOffset.Now;
        var nextIntervalSeconds = config.IdleIntervalSeconds;

        try
        {
            string raw;
            try
            {
                raw = await api.GetMowerRawAsync(mowerId);
            }
            catch (HttpRequestException)
            {
                // Likely an expired token on a long-running session - re-auth once and retry.
                await api.AuthenticateAsync();
                raw = await api.GetMowerRawAsync(mowerId);
            }

            using var doc = JsonDocument.Parse(raw);
            var attributes = doc.RootElement.GetProperty("data").GetProperty("attributes");
            var activity = attributes.GetProperty("mower").GetProperty("activity").GetString() ?? "";

            // The mower payload already carries the schedule - update the
            // cache from it for free instead of a separate daily fetch.
            tasks = attributes.TryGetProperty("calendar", out var calendarElement)
                ? calendarElement.Deserialize<CalendarInfo>()?.Tasks ?? []
                : [];
            SaveScheduleForMower(mowerId, mowerName, tasks);

            var atCharger = IsAtCharger(activity);
            var wasAtCharger = lastActivity is not null && IsAtCharger(lastActivity);
            var withinSchedule = IsWithinSchedule(tasks, timestamp);
            var nighttime = IsNighttime(timestamp, config.NightStartHour, config.NightEndHour);

            string reason;
            if (!atCharger || withinSchedule)
            {
                nextIntervalSeconds = activeIntervalSeconds;
                reason = !atCharger ? "active" : "scheduled window";
            }
            else if (nighttime)
            {
                nextIntervalSeconds = config.NightIntervalSeconds;
                reason = "night";
            }
            else
            {
                nextIntervalSeconds = config.IdleIntervalSeconds;
                reason = "idle";
            }

            if (!(atCharger && wasAtCharger))
            {
                var byteCount = Encoding.UTF8.GetByteCount(raw);
                var record = new
                {
                    timestamp,
                    mowerId,
                    mowerName,
                    bytes = byteCount,
                    response = doc.RootElement,
                };
                await File.AppendAllTextAsync(logPath, JsonSerializer.Serialize(record) + Environment.NewLine, cts.Token);

                recordCount++;
                totalBytes += byteCount;
                Console.WriteLine($"[{timestamp:yyyy-MM-dd HH:mm:ss}] logged (activity: {activity}, {byteCount} bytes, next check in {nextIntervalSeconds}s - {reason}) - " +
                                   $"{recordCount} records, {totalBytes / 1024.0:F1} KB this session");
            }
            else
            {
                Console.WriteLine($"[{timestamp:yyyy-MM-dd HH:mm:ss}] skipped (still at charger, next check in {nextIntervalSeconds}s - {reason})");
            }

            lastActivity = activity;
        }
        catch (OperationCanceledException)
        {
            break;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[{timestamp:yyyy-MM-dd HH:mm:ss}] poll failed: {ex.Message}");
        }

        try
        {
            await Task.Delay(TimeSpan.FromSeconds(nextIntervalSeconds), cts.Token);
        }
        catch (OperationCanceledException)
        {
            break;
        }
    }

    Console.WriteLine($"Stopped. {recordCount} records logged, {totalBytes / 1024.0:F1} KB this session. Log file: {logPath}");
}

async Task CommandSchedule(string[] scheduleArgs)
{
    var resolved = await ResolveMowerAsync(scheduleArgs.FirstOrDefault());
    if (resolved is null) return;
    var (mowerId, mowerName) = resolved.Value;

    var api = GetClient();
    await api.AuthenticateAsync();
    var mower = await api.GetMowerAsync(mowerId);
    var tasks = mower.Attributes.Calendar?.Tasks ?? [];

    SaveScheduleForMower(mowerId, mowerName, tasks);

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

    var nextCalendar = NextCalendarStart(tasks, DateTimeOffset.Now);
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

    var resolved = await ResolveMowerAsync(mowerQuery);
    if (resolved is null) return;
    var (_, mowerName) = resolved.Value;

    var logPath = Storage.GetTrackLogPath(mowerName);
    if (!File.Exists(logPath))
    {
        Console.WriteLine($"No track log found for {mowerName} at {logPath}.");
        return;
    }

    var points = new List<(DateTimeOffset Time, string Activity, int Battery, long WorkAreaId, long PlannerNextStart)>();
    var workAreaNames = new Dictionary<long, string>();
    var latestCalendarTasks = Array.Empty<CalendarTask>();
    var skipped = 0;
    foreach (var line in File.ReadLines(logPath))
    {
        if (string.IsNullOrWhiteSpace(line)) continue;
        try
        {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            var timestamp = root.GetProperty("timestamp").GetDateTimeOffset();
            var attributes = root.GetProperty("response").GetProperty("data").GetProperty("attributes");
            var mowerObj = attributes.GetProperty("mower");
            var activity = mowerObj.GetProperty("activity").GetString() ?? "UNKNOWN";
            var workAreaId = mowerObj.TryGetProperty("workAreaId", out var waIdEl) ? waIdEl.GetInt64() : 0L;
            var battery = attributes.GetProperty("battery").GetProperty("batteryPercent").GetInt32();
            var plannerNextStart = attributes.TryGetProperty("planner", out var plannerEl) &&
                plannerEl.TryGetProperty("nextStartTimestamp", out var nextStartEl)
                ? nextStartEl.GetInt64()
                : 0L;
            points.Add((timestamp, activity, battery, workAreaId, plannerNextStart));

            if (attributes.TryGetProperty("workAreas", out var workAreasEl) && workAreasEl.ValueKind == JsonValueKind.Array)
            {
                foreach (var wa in workAreasEl.EnumerateArray())
                {
                    if (wa.TryGetProperty("workAreaId", out var idEl) &&
                        wa.TryGetProperty("name", out var nameEl) &&
                        !string.IsNullOrWhiteSpace(nameEl.GetString()))
                    {
                        workAreaNames[idEl.GetInt64()] = nameEl.GetString()!.Trim();
                    }
                }
            }

            if (attributes.TryGetProperty("calendar", out var calendarEl) &&
                calendarEl.Deserialize<CalendarInfo>() is { Tasks.Length: > 0 } calendarInfo)
            {
                latestCalendarTasks = calendarInfo.Tasks;
            }
        }
        catch (Exception)
        {
            skipped++;
        }
    }

    if (points.Count == 0)
    {
        Console.WriteLine($"Track log for {mowerName} has no readable records.");
        return;
    }

    points.Sort((a, b) => a.Time.CompareTo(b.Time));

    // Grouping has to scan oldest-to-newest (each session's end depends on
    // the *next* point chronologically), but the printed order is
    // newest-first - so build the lines here, then print them reversed.
    var lines = new List<string>();
    var i = 0;
    while (i < points.Count)
    {
        var activity = points[i].Activity;
        var workAreaId = points[i].WorkAreaId;
        var start = points[i].Time;
        var batteryStart = points[i].Battery;

        var j = i;
        while (j + 1 < points.Count && points[j + 1].Activity == activity && points[j + 1].WorkAreaId == workAreaId)
        {
            j++;
        }
        var batteryEnd = points[j].Battery;
        var end = j + 1 < points.Count ? points[j + 1].Time : (DateTimeOffset?)null;

        var endLabel = end is null ? "ongoing" : end.Value.ToString("HH:mm", CultureInfo.InvariantCulture);
        var duration = (end ?? DateTimeOffset.Now) - start;

        var durationLabel = $"({FormatDuration(duration)})";
        var workAreaLabel = workAreaNames.TryGetValue(workAreaId, out var waName) ? $"  [{waName}]" : "";
        var sessionLine =
            $"  {start:yyyy-MM-dd}  {DescribeActivity(activity),-11} {start:HH:mm}-{endLabel,-7} " +
            $"{durationLabel,-9}  battery {batteryStart,3}% -> {batteryEnd,3}%{workAreaLabel}";

        if (showCalendar && IsAtCharger(activity))
        {
            var nextCalendar = NextCalendarStart(latestCalendarTasks, start);
            var nextCalendarLabel = nextCalendar is null ? "none" : nextCalendar.Value.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
            var plannerNextStart = points[i].PlannerNextStart;
            var nextPlannedLabel = plannerNextStart > 0
                ? DateTimeOffset.FromUnixTimeMilliseconds(plannerNextStart).ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)
                : "not scheduled";
            sessionLine += $"  next calendar start: {nextCalendarLabel}   next planned start: {nextPlannedLabel}";
        }

        lines.Add(sessionLine);

        i = j + 1;
    }

    Console.WriteLine($"Sessions for {mowerName} (newest first, from {logPath}):");
    for (var k = lines.Count - 1; k >= 0; k--)
    {
        Console.WriteLine(lines[k]);
    }

    if (skipped > 0)
    {
        Console.WriteLine($"  ({skipped} malformed line(s) skipped)");
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

string FormatDuration(TimeSpan span)
{
    var totalMinutes = Math.Max(0, (int)span.TotalMinutes);
    var hours = totalMinutes / 60;
    var minutes = totalMinutes % 60;
    return hours > 0 ? $"{hours}h{minutes:D2}m" : $"{minutes}m";
}

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
                                                 (22:00-08:00) - all configurable via 'config'. Skips
                                                 repeat polls while parked at the charger.
          automower sessions [--calendar] [mower]
                                                 Summarize track-<mower>.jsonl into one line per
                                                 mowing/charging/etc. session (split on activity or
                                                 work area changing): date, start-end time, duration,
                                                 battery start% -> end%, and work area name. --calendar
                                                 adds the next calendar/planned start (as of that poll)
                                                 under each Charging/Parked session
          automower help                        Show this help
        """);
}
