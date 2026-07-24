# AutomowerConsole

A small C# console client for the Husqvarna Automower Connect API. Authenticates
with an app key/secret via OAuth2 client-credentials, then lets you inspect
mower status, messages, work areas, stay-out zones, and schedules, plus an
adaptive polling/logging mode (`track`).

## Prerequisites

- .NET 10 SDK
- A Husqvarna Developer Portal application (https://developer.husqvarnagroup.cloud/)
  subscribed to both the **Authentication API** and the **Automower Connect API**,
  giving you an application (app) key and secret
- On a fresh Linux container/host (e.g. a new QNAP Container Station
  container): `git`, `curl`, `tmux` (for `startall.sh`/`stopall.sh`), and the
  system timezone set to match the mowers' own configured local time (not
  the container default, often UTC — see **`track`: adaptive polling and
  logging** for why this matters). Run `./bootstrap.sh` as root to install
  all of the above plus the .NET SDK in one idempotent pass.

## Setup

1. Build once to restore/compile:

   ```
   dotnet build
   ```

2. Set your credentials with the `config` command (creates `.config/config.json`
   if it doesn't exist yet):

   ```
   dotnet run -- config AppKey=your-app-key AppSecret=your-app-secret
   ```

   `config` also accepts any other config field the same way (see
   **`track`: adaptive polling and logging** below for the full list, e.g.
   `IdleIntervalSeconds=240`). Run `dotnet run -- config` with no arguments to
   print the current values (secrets masked). `config.example.json` (repo
   root, tracked) documents the full field set as a reference.

   The config file lives in `.config/config.json`, and `list`/`use`/`track`
   generate state in `.data/` — both are resolved relative to the repo root
   (found by walking up from the built executable to the nearest `.slnx`),
   not the `bin/` build output folder, so `dotnet clean` never touches them.
   Both directories are gitignored — keep it that way (see **Security note**).
   `AutomowerWeb` (see **Web dashboard**) reads the same two directories, so
   it needs to run somewhere that can see them too.

## Running

Either:

```
dotnet run -- <command> [args]
```

or use the shortcut for your platform, which builds once and then runs the
compiled binary directly:

```
am.cmd <command> [args]        # Windows
./am.sh <command> [args]       # Linux/macOS
```

On Linux/macOS, `am.sh`/`startall.sh`/`stopall.sh`/`bootstrap.sh` need the
executable bit, which git now tracks directly (`git update-index --chmod=+x`
was applied and committed) — a fresh `git clone` on Linux gets it for free.
If an *existing* checkout still loses it after a pull (some git configs,
e.g. `core.fileMode=false`, won't restore local bits from the index), run
`./fix-permissions.sh` to reset all of this repo's `*.sh` scripts at once,
or `chmod +x am.sh` for just the one. If you'd rather not chmod anything,
`bash am.sh <command>` works too without it.

**Use the shortcuts, not `dotnet run`, for `track`.** `dotnet run` is a
build-and-launch wrapper, and it does not reliably forward POSIX signals
(SIGINT/Ctrl+C) to the process it spawns — a `track` session started with
`dotnet run` can't be stopped cleanly with Ctrl+C or `kill -INT`; only
`kill -9` gets through, and you lose the graceful summary (log data is still
safe either way, since every poll is flushed to disk immediately). `am.cmd`/
`am.sh` avoid this by launching the built `.dll` directly — no wrapper
process in between.

## Commands

Commands that act on "the active mower" (the one set via `use`) accept an
optional trailing `[mower]` argument — a name, id, or list index — to target
a different mower for just that one call, without changing the active
selection.

| Command | Description |
|---|---|
| `config` | Show current config values (secrets masked) |
| `config Key=Value ...` | Set one or more config values, e.g. `config AppKey=xxx AppSecret=yyy` |
| `list` | Fetch and list all mowers on the account, save to `.data/mowers.json` |
| `use <name\|id\|index>` | Set the active mower (stored in `.data/state.json`) |
| `current` | Show the currently active mower |
| `status [--all] [mower]` | Show current status; `--all` dumps the full raw JSON payload |
| `messages [mower]` | Show message/error history, newest first, with human-readable descriptions |
| `errorcodes` | Show the full error code → description table |
| `workareas [mower]` | List all work areas |
| `workarea <name\|id> [mower]` | Detailed info for one work area, including its schedule |
| `stayoutzones [mower]` | List configured stay-out zones |
| `schedule [mower]` | Show the calendar, refresh `.data/schedule.json`, and show the live next calendar/planned start |
| `track [seconds] [mower]` | Adaptive-interval polling with logging to a per-mower `.data/track-<mower>.jsonl` (see below) |
| `sessions [--calendar] [mower]` | Summarize a mower's track log into one line per mowing/charging/etc. session (see below) |
| `daily [mower]` | One line per calendar day: total Mowing time per work area, then total Charging time (see below) |
| `help` | Show usage |

### Examples

```
am list
am use "AM430X NERA"
am status
am status "AM405X"          # check a different mower without switching active
am workarea nederside
am messages
am track                    # start adaptive polling for the active mower
```

## `track`: adaptive polling and logging

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

The schedule used to detect "scheduled window" comes from `.data/schedule.json`,
refreshed for free from every `track` poll (the mower payload already
includes the calendar — no extra API call). Run `schedule [mower]` on its
own to force a refresh without starting `track`.

Press Ctrl+C to stop tracking; already-written log lines are never lost
since each poll is flushed to disk immediately.

### `sessions`: summarizing a track log

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

### `daily`: activity totals per calendar day

`daily [mower]` rolls `sessions`' output up by day: total **Mowing** time per
work area that day (repeated on the line for each additional area worked,
summed together if the same area was mowed more than once that day), then a
single combined **Charging** total last — charging isn't tied to a work
area, so it's outside that list rather than part of it:

```
Daily activity for AM405X (newest first, from .data/track-AM405X.jsonl):
  2026-07-21  Mowing 50m [Front Lawn]   Mowing 30m [Back Yard]   Charging 21h15m
  2026-07-20  Mowing 1h00m [Front Lawn]   Mowing 45m [Back Yard]   Charging 21h15m
```

`Charging` combines `CHARGING` and `PARKED_IN_CS` into one "time spent at
the charger" total. Days with only charging (or only mowing) simply omit the
other half of the line. Other activities (`Going home`, `Leaving`,
`Stopped`, ...) aren't represented — only the two totals that were asked for.

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

### `calendar` vs `planner`

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

`schedule [mower]` shows both: the calendar (refreshed into
`.data/schedule.json`), plus the live "Next calendar start" / "Next planned
start" pair and any active `restrictedReason`.

### Running `track` unattended (e.g. over SSH / `docker exec`)

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
time (`tmux new -s automower-405x`, etc. — see **Commands** for the
`[mower]` override).

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

## Config and generated files

Both live in the repo root, resolved at runtime by walking up from the built
executable to the nearest `.csproj` — not `bin/`, so a `dotnet clean` (which
wipes `bin/`/`obj/`) never touches either of them. Both are gitignored.

| Path | Contents |
|---|---|
| `.config/config.json` | App key/secret + `track` interval settings (via `config`) |
| `.data/mowers.json` | Cached list of mowers on the account (from `list`) |
| `.data/state.json` | The active mower selection (from `use`) |
| `.data/schedule.json` | Cached per-mower calendar, keyed by mower id (from `schedule` or `track`) |
| `.data/track-<mower name>.jsonl` | Append-only log of polls from `track`, one file per mower |

## Security note

`.config/config.json` contains your Husqvarna app key and secret in plain
text. It's already gitignored — don't remove that entry, and don't commit
the file directly. `config.example.json` (repo root, tracked) is the
placeholder template to copy from if you ever need to recreate it by hand;
`config AppKey=... AppSecret=...` does the same thing without manual editing.

## Project layout

Four projects under `automower.slnx`: a shared library, the CLI, its tests,
and a web dashboard — the CLI and the web app are two independent
presentation layers over the same domain/service code, not one depending on
the other.

- **`AutomowerConsole.Core/`** — the shared domain/service layer. Everything
  in here is what used to live directly in `AutomowerConsole/` before the
  CLI and `AutomowerWeb` both needed it; `public` is a real API boundary
  here now, not the `internal` + `InternalsVisibleTo` pattern still used
  for test-only access:
  - `MowerService.cs` — mower listing, caching, and name/id/index resolution
  - `MowerDetailService.cs` — fetching a specific mower's live status,
    messages, and work area detail
  - `ScheduleService.cs` — calendar/schedule calculations and the schedule
    cache
  - `TrackingService.cs` — the `track` polling loop and `sessions`/`daily`
    log summarization
  - `ErrorCodes.cs`, `Extensions.cs` (`FormatDuration`, `IsNighttime`) — small
    public helpers both consumers use for display
  - `AutomowerConnect.cs` / `HusqvarnaClient.cs` — auth + raw HTTP calls,
    deliberately kept `internal` to Core — nothing outside Core, in either
    the CLI or the web app, should reach the API directly; go through the
    services above instead
  - `Storage.cs` — reads/writes `.config/config.json` and `.data/*.json(l)`,
    and finds the repo root (nearest `.slnx`, not `.csproj` — there's only
    ever one, and it stays in the true repo root regardless of how many
    projects sit under it) that they're anchored to. `public`, unlike the
    other internals above, since the CLI's own config/state commands
    (`config`, `use`, `current`) call it directly with no service layer of
    their own
  - `Models.cs` — JSON response models and config/cache record types (the
    pure wire-DTOs the API's JSON unwraps into stay `internal`; the actual
    domain types services return are `public`)
- **`AutomowerConsole/`** — the CLI. Just `Program.cs` now: argument
  parsing and result printing on top of `AutomowerConsole.Core`'s services
- **`AutomowerConsole.Tests/`** — NUnit tests, referencing
  `AutomowerConsole.Core` directly (it's what they've always actually
  tested — `TrackingService`, etc.)
- **`AutomowerWeb/`** — the Blazor web dashboard, see **Web dashboard**
  below
- `am.cmd` / `am.sh` / `automower.slnx` at the repo root

Run the tests with `dotnet test`.

## Web dashboard (`AutomowerWeb`)

A read-only Blazor Server app: a `/` dashboard (live status per mower —
activity, battery, work area, connected, next start — plus that mower's
sessions from *today only*) and a `/mower/{name}` details page per mower
(full session history, daily rollup, work areas, stay-out zones, schedule,
recent messages). No login yet, and deliberately no mower control anywhere
in it — an unauthenticated public control surface for a physical outdoor
device is a different risk class than an unauthenticated read-only
dashboard, and hasn't been asked for.

Run it locally the same way as any ASP.NET project, from the repo root so
it can see `.config`/`.data`:

```
dotnet run --project AutomowerWeb
```

then open the URL it prints (default `http://localhost:5152`).

**No auto-refresh timer on the dashboard, by design.** It's a 4th
independent process authenticating with the same Husqvarna app key/secret
as the 3 `track` sessions (see `AutomowerConsole`'s `startall.sh` notes on
Husqvarna's `simultaneous.logins` rejection) — a background poll loop would
add another recurring source of auth traffic for a dashboard nobody's
continuously watching. It loads once per page visit and on an explicit
"🔄 Refresh" click instead.

**Not yet deployed anywhere** — running it in its own container (separate
from the one running `track`), exposing it via router port-forwarding, and
adding auth are all separate, later steps, not part of what's built here.
- `am.cmd` / `am.sh` — shortcuts that build `AutomowerConsole.csproj` once
  and then run the compiled `.dll` directly (not `dotnet run` — see above)
- `startall.sh` / `stopall.sh` — start/stop one tmux `track` session per
  mower (see **Running `track` unattended** above)

For API implementation notes (auth flow, endpoint quirks, timestamp units,
external references) see `.claude/skills/automower-api/SKILL.md`.
