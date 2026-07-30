# CLI usage

Assumes you've already completed [`installation.md`](installation.md) (built
once, set `AppKey`/`AppSecret` via `config`).

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

**Use the shortcuts, not `dotnet run`, for `track`/`hybrid-track`.**
`dotnet run` is a build-and-launch wrapper, and it does not reliably forward
POSIX signals (SIGINT/Ctrl+C) to the process it spawns — a tracking session
started with `dotnet run` can't be stopped cleanly with Ctrl+C or
`kill -INT`; only `kill -9` gets through, and you lose the graceful summary
(log data is still safe either way, since every poll/event is written
immediately). `am.cmd`/`am.sh` avoid this by launching the built `.dll`
directly — no wrapper process in between.

## Commands

Commands that act on "the active mower" (the one set via `use`) accept an
optional trailing `[mower]` argument — a name, id, or list index — to target
a different mower for just that one call, without changing the active
selection.

| Command | Description |
|---|---|
| `config` | Show current config values (secrets masked) |
| `config Key=Value ...` | Set one or more config values, e.g. `config AppKey=xxx AppSecret=yyy` |
| `list` | Fetch and list all mowers on the account, save to the mower registry (`.data/common.db`) |
| `use <name\|id\|index>` | Set the active mower (stored in `.data/state.json`) |
| `current` | Show the currently active mower |
| `status [--all] [mower]` | Show current status; `--all` dumps the full raw JSON payload |
| `messages [mower]` | Show message/error history, newest first, with human-readable descriptions |
| `errorcodes` | Show the full error code → description table |
| `workareas [mower]` | List all work areas |
| `workarea <name\|id> [mower]` | Detailed info for one work area, including its schedule |
| `stayoutzones [mower]` | List configured stay-out zones |
| `schedule [mower]` | Show the calendar, refresh the cached per-mower schedule, and show the live next calendar/planned start |
| `hybrid-track [mower]` | **Default tracker** (what `startall.sh` runs) - WebSocket events drive live status with near-instant precision, a slow REST refresh keeps statistics/schedule current. Logs to `.data/mower-<name>.db` (see [`database-schema.md`](database-schema.md)) |
| `track [seconds] [mower]` | The original adaptive-interval REST-only poller (see [`tracking.md`](tracking.md)) - still available, also logs to `.data/mower-<name>.db` now, just without event-driven precision |
| `sessions [--calendar] [mower]` | Summarize a mower's history into one line per mowing/charging/etc. session |
| `daily [mower]` | One line per calendar day: total Mowing time per work area, then Charging and Parked time |
| `seasons [mower]` | Season-over-season growth in lifetime running/cutting/charging time, charging cycles, blade usage, and drive distance |
| `baseline <YYYY-MM-DD> [mower]` | Seed an all-zero daily-statistics record on a past date (e.g. a mower's purchase date) so `seasons` has a real day-zero to diff against |
| `migrate-to-sqlite [mower]` | One-time dev tool: migrates a mower's old JSONL-backed history into SQLite (already run for all 3 mowers as of the 2026-07-30 cutover) |
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

## Deeper dives

`hybrid-track` (what `startall.sh` runs) and the original `track` both log
each mower's history to its own SQLite db (`.data/mower-<name>.db` - see
[`database-schema.md`](database-schema.md)); `sessions`/`daily`
summarize that history; `seasons`/`baseline` track season-over-season
lifetime-statistics growth. Adaptive polling intervals, the event-driven
design, the `calendar` vs `planner` distinction, and running trackers
unattended (tmux, `startall.sh`/`stopall.sh`) are all covered in
**[`tracking.md`](tracking.md)**.
