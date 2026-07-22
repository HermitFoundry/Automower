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
   (found by walking up from the built executable to the nearest `.csproj`),
   not the `bin/` build output folder, so `dotnet clean` never touches them.
   Both directories are gitignored — keep it that way (see **Security note**).

## Running

Either:

```
dotnet run -- <command> [args]
```

or use the shortcut for your platform, which builds once and then runs the
compiled binary directly:

```
am.cmd <command> [args]        # Windows
./am.sh <command> [args]       # Linux/macOS - chmod +x am.sh once if needed
```

**Use the shortcuts, not `dotnet run`, for `track`.** `dotnet run` is a
build-and-launch wrapper, and it does not reliably forward POSIX signals
(SIGINT/Ctrl+C) to the process it spawns — a `track` session started with
`dotnet run` can't be stopped cleanly with Ctrl+C or `kill -INT`; only
`kill -9` gets through, and you lose the graceful summary (log data is still
safe either way, since every poll is flushed to disk immediately). `am.cmd`/
`am.sh` avoid this by launching the built `.dll` directly — no wrapper
process in between.

## Migrating an existing checkout

If you have an older checkout where `config.json` and the generated
`mowers.json`/`state.json`/`schedule.json`/`track.jsonl` still live under
`bin/` (from before those moved to `.config/`/`.data/`), run this once to
move everything into place without losing anything — including splitting an
old combined `track.jsonl` into the current per-mower log files (using each
line's own `mowerName` field via `awk`, no extra dependency), merging into
any per-mower file that already exists rather than overwriting it.

```
./migrate-to-dotfolders.sh
```

Safe to re-run; it skips anything already migrated.

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

- `Program.cs` — CLI entry point and command implementations
- `HusqvarnaClient.cs` — OAuth2 authentication and Automower Connect API calls
- `Models.cs` — JSON response models and config/cache record types
- `Storage.cs` — reads/writes `.config/config.json` and `.data/*.json(l)`, and
  finds the repo root that they're anchored to
- `ErrorCodes.cs` — full Automower error code → description table
- `am.cmd` / `am.sh` — shortcuts that forward arguments to `dotnet run`
- `migrate-to-dotfolders.sh` — one-time migration from the old `bin/`-based
  config/data layout to `.config/`/`.data/`

For API implementation notes (auth flow, endpoint quirks, timestamp units,
external references) see `.claude/skills/automower-api/SKILL.md`.
