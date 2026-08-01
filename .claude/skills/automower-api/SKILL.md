---
name: automower-api
description: This skill should be used when working in the automower repo - this project's own code structure, CLI commands, and implementation history around the Husqvarna Automower Connect API. For the vendor API itself (auth, endpoints, WebSocket behavior, error codes, timestamp gotchas) not tied to this specific codebase, see the global husqvarna-automower-api skill / John agent instead. Triggers on "automower", "husqvarna", "mower api", "am.cmd".
version: 2.0.0
---

# Automower Connect API notes - this repo's implementation

This project's own code structure, CLI behavior, and implementation
history. **The Husqvarna Automower Connect API itself** - authentication,
REST endpoint shapes, rate limits, WebSocket event types/connection
lifecycle, timestamp-unit gotchas, ambiguous status fields, the full error
code table, and real-world event-stream behavior findings - now lives in
the global **`husqvarna-automower-api`** skill (used by the **John** agent)
so it's not duplicated here and stays available in any project, not just
this one. Reach for that skill/agent for anything about what the vendor
API does; this file is about what *this codebase* does with it.

Working knowledge for `C:\repos\automower`, a 4-project net10.0 solution
(`automower.slnx`) talking to the Husqvarna Automower Connect API:
`AutomowerConsole.Core` (shared domain/service layer - `MowerService`,
`MowerDetailService`, `ScheduleService`, `TrackingService`, `AutomowerConnect`,
`Storage`, `Models`, etc., all `public` except `AutomowerConnect`/
`HusqvarnaClient`/wire-DTOs which stay `internal` to Core - nothing outside
Core should reach the API directly), `AutomowerConsole` (the CLI, just
`Program.cs` on top of Core), `AutomowerConsole.Tests` (NUnit, tests Core
directly), and `AutomowerWeb` (a read-only Blazor Server dashboard, also on
top of Core - see README's "Web dashboard" section for what it shows and
how to run it; `dotnet run --project AutomowerWeb`). The CLI and the web app
are two independent presentation layers over the same Core, neither depends
on the other. Run the CLI via `am.cmd <command> [args]` on Windows or
`./am.sh <command> [args]` on Linux/macOS, or `dotnet run --project
AutomowerConsole -- <command>` directly for quick one-offs. Also deployed to
a Debian container on the QNAP NAS for the long-running `hybrid-track`
daemons - see `.claude/skills/qnap-ops/SKILL.md` for that side of things
(and the global `qnap-container-station`/`docker-ssh-remote-ops` skills /
**Tim** agent for the general QNAP/Docker/SSH techniques behind it).

Session naming and the mower query passed to `track`/`hybrid-track` both
use just the **model prefix** of each mower's name - `${name%% *}` in bash,
e.g. "AM430X" out of "AM430X NERA" - relying on the CLI's existing
name-contains matching (`MowerService.FindMower`) to resolve that shortened
form back to the full mower. **Only safe while each model prefix is unique
across the account** (true for the current 3, one of each model) - if a
second mower ever shared a prefix, this would need to fall back to the full
name or the mower id instead, noted directly in `startall.sh`'s own
comments too.

**`startall.sh`'s historical "only 1 of 3 sessions started" bug** had two
independent causes, both fixed: (1) a concurrent-`dotnet build` race
between the 3 mowers' processes launching at once (fixed by building once
up front, running the built `.dll` directly instead of going through
`am.sh`), and (2) all 3 processes authenticating within milliseconds of
each other, which Husqvarna's auth service rejects as suspicious
concurrent logins for one client id - see the global
`husqvarna-automower-api` skill's Authentication section for why that
rejection happens and how it's generally handled; this repo's specific fix
is `startall.sh` staggering session starts with a 5s `sleep` between each
one actually launched (comfortably longer than one token round-trip).

**Repo layout**: the console app lives in `AutomowerConsole/` (its own
subfolder), with `automower.slnx` (solution file) plus `am.cmd`/`am.sh` at
the true repo root - moved there specifically to fix a bare `dotnet build`
failing with "more than one project or solution file" once `automower.slnx`
appeared alongside the `.csproj` at the repo root.

**`AutomowerConsole.Tests/`** (sibling folder, NUnit, referenced by
`automower.slnx`) - `TrackingServiceTests.cs` exercises
`TrackingService.AggregateDailyActivity` (a pure, static method,
deliberately extracted from `SummarizeDailyActivity` for exactly this
reason: testable without a real track log/db) against real session
history reproduced as `TrackSession` fixture data. One test specifically
locks in the overnight-session/midnight-attribution behavior using a real
cross-midnight session. References `AutomowerConsole.Core` directly.

**Domain nuances confirmed while working with real session data** - the
underlying vendor-API facts (activity labels not being a reliable charging
signal, a parked mower's battery drifting slightly) now live in the global
skill's Gotchas section and `resources/websocket-real-world-findings.md`.
This project's own conclusion from that: keep `CHARGING`/`PARKED_IN_CS`
summed as one combined "at the charger" total (`IsAtCharger`) rather than
attempting battery-delta-based accuracy - a deliberate choice, not an
oversight, if this comes up again.

**`track` used to only log the arrival poll and skip repeats while still
parked at the charger (changed 2026-07-27) - now every poll is logged,
same as any other activity.** The old skip logic could leave a real gap:
if the mower's own `activity` label flipped from `CHARGING` to
`PARKED_IN_CS` on the exact poll that crossed 100%, that poll started the
*next* session instead of ending the charging one, leaving that session's
`BatteryEnd` stuck at whatever it was on arrival - confirmed against a real
case. `SummarizeSessions` now calls `TrackingService
.BackfillChargingEndBattery` as a display-layer correction: when a
charging-type session's `End` exactly equals the next at-charger session's
`Start` (no gap) and that next session's `BatteryStart` is higher, the
first session's `BatteryEnd` is backfilled from it. Doesn't touch
`AggregateDailyActivity`/`AggregateMonthlyActivity` totals (those derive
duration from `Start`/`End`/`ChargeCompleteAt`, never `BatteryEnd`) -
purely a session-list display fix.

**`am.cmd`/`am.sh` build once then launch the compiled `.dll` directly -
deliberately not `dotnet run`.** Confirmed by direct incident: a `track`
session started via `dotnet run` could not be stopped with Ctrl+C or
`kill -INT <pid>`; only `kill -9` worked. Root cause: `dotnet run` is a
build-and-launch wrapper process that does not reliably forward POSIX
signals to the child process it spawns - never recommend `dotnet run` for
anything long-running in this repo; always the built binary via the
shortcut scripts. Log/db data itself was never at risk either way (every
poll/event is written immediately) - the only casualty of the SIGKILL
workaround was the graceful stop summary line.

## Project layout

Four-layer split: `Program.cs` (CLI dispatch + printing) → service classes
(data-gathering/calculation) → `AutomowerConnect` (auth-handling facade,
reached via a static singleton `AutomowerConnect.Instance`) →
`HusqvarnaClient` (raw HTTP). See `docs/design.md` for the current,
authoritative breakdown of `AutomowerConsole.Core`'s services (this
section used to duplicate that here; kept in one place now to avoid drift).

**`AutomowerConnect.Instance` must stay lazy**: `Storage.LoadConfig()`
throws if `AppKey`/`AppSecret` aren't set yet, and `help`/`config`/
`errorcodes`/`current` must keep working with zero `config.json` present -
this is why none of the services' constructors may touch
`AutomowerConnect.Instance` themselves, only method bodies that actually
make a call.

**Security history**: `config.json` with real credentials was accidentally
committed in this repo's first commit (back when it lived at the repo
root). Fixed by deleting the branch ref (`git update-ref -d
refs/heads/main` - safe since it was the only commit, no remote existed
yet) rather than just `git rm --cached`, so the secret never existed in
any reachable git history. If this ever happens again *after* a remote
exists, deleting the ref won't be enough - history rewrite (or credential
rotation) would be needed instead.

See `docs/cli-usage.md` for the current command reference and
`docs/tracking.md` for `track`/`hybrid-track`'s polling/event design,
`sessions`/`daily` output format, and the `calendar` vs `planner`
distinction as displayed by this CLI (the underlying vendor-API concepts
behind that distinction are in the global skill).

## This account's mowers (as of 2026-07-21)

| Name        | Model                          | Serial    |
|-------------|---------------------------------|-----------|
| AM405X      | Husqvarna Automower® 405X       | 210505449 |
| AM430X NERA | Husqvarna Automower® 430X NERA  | 232802869 |
| AM308V Nede | Automower® 308V                 | 260802646 |

(Authoritative live copy is `.data/common.db`, regenerated by `am list`.)
