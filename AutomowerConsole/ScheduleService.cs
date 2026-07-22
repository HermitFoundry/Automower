namespace AutomowerConsole;

// Calendar/schedule calculations and the schedule.json cache. Pure
// calculation plus Storage-backed caching - no AutomowerConnect dependency.
// DateTimeOffset.IsNighttime(...) (Extensions.cs) is a separate, pre-existing
// piece of schedule-timing logic; this class calls it rather than duplicating it.
internal class ScheduleService
{
    public bool DayFlag(CalendarTask t, DayOfWeek day) => day switch
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
    public bool IsWithinSchedule(CalendarTask[] tasks, DateTimeOffset now)
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
    public DateTimeOffset? NextCalendarStart(CalendarTask[] tasks, DateTimeOffset after)
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

    public void SaveScheduleForMower(string mowerId, string mowerName, CalendarTask[] tasks)
    {
        var schedules = Storage.LoadSchedules();
        schedules[mowerId] = new MowerSchedule(mowerName, DateTimeOffset.Now, tasks);
        Storage.SaveSchedules(schedules);
    }

    public CalendarTask[] GetCachedTasks(string mowerId)
        => Storage.LoadSchedules().TryGetValue(mowerId, out var entry) ? entry.Tasks : [];

    // 'track's next-poll-interval decision: fast while active or in a
    // scheduled window (charge duration is unpredictable, so poll fast to
    // catch the exact moment it leaves), slow at night, moderate otherwise
    // (watching for a manually-started mow). 'activeIntervalSeconds' is the
    // already-resolved fast interval (config default, or the CLI's
    // 'track [seconds]' override) - not re-derived from config here.
    public (int Seconds, string Reason) DetermineTrackingInterval(
        CalendarTask[] tasks, string activity, int activeIntervalSeconds, DateTimeOffset now, Config config)
    {
        var atCharger = TrackingService.IsAtCharger(activity);
        var withinSchedule = IsWithinSchedule(tasks, now);

        if (!atCharger || withinSchedule)
        {
            return (activeIntervalSeconds, !atCharger ? "active" : "scheduled window");
        }

        if (now.IsNighttime(config.NightStartHour, config.NightEndHour))
        {
            return (config.NightIntervalSeconds, "night");
        }

        return (config.IdleIntervalSeconds, "idle");
    }
}
