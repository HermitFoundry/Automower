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
  the container default, often UTC — see
  [`docs/tracking.md`](docs/tracking.md) for why this matters). Run
  `./bootstrap.sh` as root to install all of the above plus the .NET SDK in
  one idempotent pass.

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
   [`docs/tracking.md`](docs/tracking.md) for the full list, e.g.
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
| `hybrid-track [mower]` | **Default tracker** (what `startall.sh` runs) - WebSocket events drive live status with near-instant precision, a slow REST refresh keeps statistics/schedule current. Logs to `.data/mower-<name>.db` (see [`docs/database-schema.md`](docs/database-schema.md)) |
| `track [seconds] [mower]` | The original adaptive-interval REST-only poller (see [`docs/tracking.md`](docs/tracking.md)) - still available, also logs to `.data/mower-<name>.db` now, just without event-driven precision |
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

## `hybrid-track`, `track`, `sessions`, `daily`, `seasons`

`hybrid-track` (what `startall.sh` runs) and the original `track` both log
each mower's history to its own SQLite db (`.data/mower-<name>.db` - see
[`docs/database-schema.md`](docs/database-schema.md)); `sessions`/`daily`
summarize that history; `seasons`/`baseline` track season-over-season
lifetime-statistics growth. Adaptive polling intervals, the event-driven
design, the `calendar` vs `planner` distinction, and running trackers
unattended (tmux, `startall.sh`/`stopall.sh`) are all covered in
**[`docs/tracking.md`](docs/tracking.md)**.

## Config and generated files

Both live in the repo root, resolved at runtime by walking up from the built
executable to the nearest `.csproj` — not `bin/`, so a `dotnet clean` (which
wipes `bin/`/`obj/`) never touches either of them. Both are gitignored.

| Path | Contents |
|---|---|
| `.config/config.json` | App key/secret + `track`/`hybrid-track` interval settings (via `config`) |
| `.data/state.json` | The active mower selection (from `use`) - unaffected by the SQLite migration, still a plain file |
| `.data/common.db` | Mower registry (from `list`) - see [`docs/database-schema.md`](docs/database-schema.md) |
| `.data/mower-<mower name>.db` | One SQLite db per mower - raw + derived history, cached schedule, daily statistics. See [`docs/database-schema.md`](docs/database-schema.md) for the full schema |

**Legacy JSONL files** (`.data/mowers.json`, `.data/schedule-<mower>.json`,
`.data/track-<mower>.jsonl`, `.data/events-<mower>.jsonl`,
`.data/statistics-<mower>.jsonl`) predate the 2026-07-30 SQLite cutover -
kept on disk as a historical record, but nothing reads or writes them
anymore.

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
- `am.cmd` / `am.sh` — shortcuts that build `AutomowerConsole.csproj` once
  and then run the compiled `.dll` directly (not `dotnet run` — see above)
- `startall.sh` / `stopall.sh` — start/stop one tmux `track` session per
  mower (see [`docs/tracking.md`](docs/tracking.md))
- `startweb.sh` / `stopweb.sh` — start/stop `AutomowerWeb` in a detached
  tmux session (see **Web dashboard** below)
- `bootstrap.sh` / `fix-permissions.sh` — one-time container provisioning
  and the `chmod +x` fallback (see **Prerequisites**/**Running** above)
- `automower.slnx` — the solution file referencing all four projects

Run the tests with `dotnet test`.

## Web dashboard (`AutomowerWeb`)

A read-only Blazor Server app: a `/` dashboard (live status per mower) and
a `/mower/{name}` details page per mower (full session history, work
areas, schedule, lifetime statistics, seasons, and more). Run locally with
`dotnet run --project AutomowerWeb` (default `http://localhost:5152`).
Full details — QNAP deployment via `startweb.sh`, dev mode, the
no-auto-refresh design decision — in
**[`docs/web-dashboard.md`](docs/web-dashboard.md)**.

## Documentation

This README covers local setup and day-to-day CLI usage. Deeper/deployment
topics live in `docs/`:

| Doc | Covers |
|---|---|
| [`docs/tracking.md`](docs/tracking.md) | `track` polling intervals, `sessions`/`daily`/`seasons`, `calendar` vs `planner`, running `track` unattended |
| [`docs/web-dashboard.md`](docs/web-dashboard.md) | `AutomowerWeb` — pages, external services, running it locally and on the QNAP container |
| [`docs/database-schema.md`](docs/database-schema.md) | SQLite storage backend schema (mermaid ER diagrams) |
| [`docs/deployment.md`](docs/deployment.md) | Public deployment architecture (Caddy, TLS, hostname, no-auth decision) |
| [`docs/qnap-access.md`](docs/qnap-access.md) | Getting a shell on the QNAP container over SSH, SSH-tunnel testing |
| [`docs/qnap_infrastructure_setup.md`](docs/qnap_infrastructure_setup.md) | Deeper QNAP/Container Station operational notes (timezone, port mapping, SSH forwarding) |
| [`.claude/skills/automower-api/SKILL.md`](.claude/skills/automower-api/SKILL.md) | API implementation notes — auth flow, endpoint quirks, timestamp units, WebSocket research |
