# `track`: adaptive polling and logging

`track` polls the mower's full status on an interval and appends one JSON
line per kept poll to a per-mower log file, `.data/track-<mower name>.jsonl`
(e.g. `.data/track-AM430X-NERA.jsonl`), so you can see exactly how much data
a day of monitoring costs for that mower (the log file's size on disk is the
answer). Each line is `{timestamp, mowerId, mowerName, bytes, response}`,
where `response` is the complete raw API payload for that poll. Running
`track` for multiple mowers in parallel (see **Running `track` unattended**
below) writes to separate files — there's no combined log.

The polling interval adapts to what's actually happening, in this priority
order:

1. **Active or in a scheduled mowing window** — poll fast (default 60s).
   This covers the mower actually being out mowing, and also the window
   where it's scheduled to start but might still be charging (charge
   duration isn't predictable, so we poll fast to catch the exact moment
   it leaves).
2. **Nighttime** (default 22:00–08:00) and otherwise idle — poll every
   30 minutes, since no one manually starts a mow overnight.
3. **Daytime, idle, not scheduled** — poll every 5 minutes, watching for a
   manually-started mow. If one starts, the next poll notices immediately
   and switches to the fast interval.

While the mower is parked at the charging station and none of the above
applies, only the *first* poll after arrival is logged — repeat polls while
still parked are skipped (only printed to the console), so idle time at the
dock doesn't inflate the log.

All intervals, plus the nighttime window, are configurable (defaults shown):

```json
{
  "ScheduledIntervalSeconds": 60,
  "IdleIntervalSeconds": 300,
  "NightIntervalSeconds": 1800,
  "NightStartHour": 22,
  "NightEndHour": 8
}
```

The schedule used to detect "scheduled window" comes from a per-mower
cached copy, refreshed for free from every `track` poll (the mower payload
already includes the calendar — no extra API call). Run `schedule [mower]`
on its own to force a refresh without starting `track`.

Press Ctrl+C to stop tracking; already-written log lines are never lost
since each poll is flushed to disk immediately.

## `sessions`: summarizing a track log

`sessions [--calendar] [mower]` reads that mower's `track-<mower>.jsonl` and
groups consecutive polls sharing the same `activity` **and** work area
(Mowing, Charging, Parked, Going home, Leaving, Stopped, ...) into one line
per session, newest first — a work area switch mid-`Mowing` starts a new
session even without an activity change:

```
Sessions for AM405X (newest first, from .data/track-AM405X.jsonl):
  2026-07-22  Parked      08:55-ongoing (3h05m)    battery  48% ->  48%
  2026-07-22  Going home  08:45-08:55   (10m)      battery  50% ->  50%  [Back Yard]
  2026-07-22  Mowing      08:00-08:45   (45m)      battery  70% ->  55%  [Back Yard]
  2026-07-22  Mowing      06:05-08:00   (1h55m)    battery  98% ->  70%  [Front Lawn]
  2026-07-22  Leaving     06:00-06:05   (5m)       battery 100% -> 100%
  2026-07-21  Charging    23:10-06:00   (6h50m)    battery  40% ->  40%
```

The work area name (in brackets) comes from that same poll's `workAreaId`,
resolved against the mower's `workAreas` list carried in the payload; it's
omitted when the id doesn't resolve to a named area (e.g. while charging on
some mowers, or a mower with only the single default unnamed area).

A session's end time is taken from the *next* differing poll, not its own
last poll — this matters most for charger stays, since `track` only logs one
poll on arrival and skips repeats while parked (see above), so a whole
charging session is often a single log line; using the next poll's timestamp
is the earliest point the log can actually confirm the mower left. The last
session in the file (still ongoing) shows `ongoing` instead of an end time,
with duration computed to now.

**`--calendar`** appends the next calendar start and next planned start to
each `Charging`/`Parked` session line, **as they stood at that historical
poll** (both are embedded in every poll's raw payload, so no extra API call
is needed — see **`calendar` vs `planner`** below for what each one means):

```
  2026-07-22  Parked      13:02-ongoing (7h05m)    battery  95% ->  95%  next calendar start: 2026-07-23 09:00   next planned start: 2026-07-22 16:03
```

## `daily`: activity totals per calendar day

`daily [mower]` rolls `sessions`' output up by day: total **Mowing** time per
work area that day (repeated on the line for each additional area worked,
summed together if the same area was mowed more than once that day), then
**Charging** and, if any, **Parked** last — neither is tied to a work area,
so both are outside that list rather than part of it:

```
Daily activity for AM405X (newest first, from .data/track-AM405X.jsonl):
  2026-07-21  Mowing 50m [Front Lawn]   Mowing 30m [Back Yard]   Charging 3h20m   Parked 17h55m
  2026-07-20  Mowing 1h00m [Front Lawn]   Mowing 45m [Back Yard]   Charging 21h15m
```

`Charging`+`Parked` together are "time spent at the charger" (`CHARGING` and
`PARKED_IN_CS` combined — the activity label alone is an unreliable signal
for whether real charging is happening, not split further on that axis).
What *does* split them: the poll where `track` observes battery reach 100%
(see **`track`: adaptive polling and logging** above) marks the boundary —
`Charging` is arrival → that point, `Parked` is that point → the mower
leaving again (charged, but no longer actively charging). A stay that never
reaches 100% before leaving (or is still ongoing) counts entirely as
`Charging`, with no `Parked` portion — "still charging" as far as the data
can tell; `Parked` is omitted from the line entirely when zero, same as
`Charging`/`Mowing` being omitted when a day has none. Other activities
(`Going home`, `Leaving`, `Stopped`, ...) aren't represented — only the
totals that were asked for.

**A session counts entirely toward the day it *started*** — same
simplification `sessions` already makes for its own single date column, not
something `daily` adds on top. This matters most for an *ongoing* session:
if the mower has been parked at the charger since yesterday afternoon and
still is, that entire (and growing) duration shows up under yesterday's
date, which can legitimately read as more than 24 hours — that's real
elapsed time for one continuous session, not a bug. Splitting a
session's duration across the midnight boundary it crosses would be more
literally accurate but adds real complexity; not done unless it turns out
to matter in practice.

Also see `seasons`/`baseline` (`am help`) for season-over-season lifetime
statistics growth, built from a daily snapshot `track` writes automatically.

## `calendar` vs `planner`

Two related but different things show up throughout this tool:

- **`calendar`** — the static, user-configured recurring schedule (what you
  set up in the app): a list of tasks, each with a start time, duration,
  which weekdays it applies to, and which work area. This is what
  `workarea`/`schedule` display, and what `sessions --calendar`'s "next
  calendar start" is computed from.
- **`planner`** — the mower's live, computed next-action state, derived
  *from* the calendar plus real-time factors (battery, restrictions,
  manual overrides). Its `nextStartTimestamp` is "next planned start" —
  it can differ from a naive calendar lookup, since the mower's own
  decision-making can push the actual next start later (or, in principle,
  earlier) than what the calendar alone would suggest.

`schedule [mower]` shows both: the calendar (refreshed into the cached
per-mower schedule copy), plus the live "Next calendar start" / "Next
planned start" pair and any active `restrictedReason`.

## Running `track` unattended (e.g. over SSH / `docker exec`)

`track` is meant to run for hours or days at a stretch, so it shouldn't
depend on a terminal staying open. If it's just started in a plain shell
over SSH or `docker exec`, a dropped connection can kill it along with the
shell (behavior varies, and isn't something to rely on either way).

Run it inside `tmux` (or `screen`) instead — a terminal multiplexer that
keeps the session (and anything running in it) alive on the server
independent of your connection. You attach to interact with it and detach
to leave it running in the background; reattach later, even from a
different connection, to check on it or stop it:

```
tmux new -s automower       # start a named session
./am.sh track                # run track inside it
# detach without stopping it: Ctrl+b, then d

tmux attach -t automower    # reattach later to check on it or Ctrl+C it
```

One session per mower if you're running `track` for more than one at a
time (`tmux new -s automower-405x`, etc. — see the main README's
**Commands** table for the `[mower]` override).

**Deleting a tmux session** once you're done with it — two ways:

- From inside it: stop `track` first (Ctrl+C), then exit the shell
  (`exit` or Ctrl+D). A tmux session closes itself automatically once the
  last program running inside it exits — there's nothing extra to delete.
- From outside it, without attaching (e.g. you just want to kill it and
  don't care about the summary output):

  ```
  tmux ls                          # list sessions, confirm the name
  tmux kill-session -t automower   # force-delete it, whatever's running inside dies too
  ```

  `tmux kill-session` doesn't stop `track` gracefully first — it's the
  tmux equivalent of closing the terminal window, so treat it like the
  `kill -9` fallback further up: your log data is still safe (flushed
  after every poll), you just won't get the clean summary line.

**`startall.sh` / `stopall.sh`** automate the above for every mower on the
account at once (one tmux session per mower, named `automower-<model
prefix>` — the part of the mower's name before the first space, e.g.
`automower-AM430X` for "AM430X NERA" — relying on the CLI's existing
name-contains matching to resolve that shortened form back to the full
mower; only safe while each model prefix is unique across the account, true
for the current 3):

```
./startall.sh   # one detached tmux session per mower, each running 'track'
./stopall.sh    # Ctrl+C into each session so 'track' stops gracefully,
                 # force-kills anything still around after a few seconds
```

`startall.sh` fetches the mower list first if `.data/mowers.json` doesn't
exist yet, and skips any mower whose session is already running rather than
starting a duplicate — safe to re-run. Check on things afterward the normal
tmux way (`tmux ls`, `tmux attach -t automower-<mower name>`).
