---
name: automower-api
description: This skill should be used when working in the automower repo, or discussing the Husqvarna Automower Connect API, OAuth2 client-credentials auth against api.authentication.husqvarnagroup.dev, mower status/messages/work areas/stay-out zones, Automower error codes, or the aioautomower reference project. Triggers on "automower", "husqvarna", "mower api", "am.cmd".
version: 1.0.0
---

# Automower Connect API notes

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
AutomowerConsole -- <command>` directly for quick one-offs. Also deployed to a
Debian container on the user's QNAP TS-673A NAS for long-running `track`
sessions - `startall.sh`/`stopall.sh` (repo root) automate running one tmux
`track` session per mower there (currently 3: AM405X, AM430X NERA, AM308V
Nede) rather than doing the `tmux new -s ...`/Ctrl+C-per-session dance by
hand for each. `startall.sh` discovers the mower list from `.data/mowers.json`
(fetching it first via `am.sh list` if missing) rather than hardcoding the 3
current mower names, so it stays correct if a mower is added/renamed/removed.

**Getting a shell on that container**: see README's "Connecting to the QNAP
container over SSH" section for the two-hop pattern (host, then `docker exec`
into the container) and the `ssh automower` alias shortcut - and
`qnap_infrastructure_setup.md` for the deeper QNAP-specific gotchas behind
it (`docker` not on `PATH` for non-interactive/`RemoteCommand` invocations,
Container Station's port-mapping limitations, the `AllowTcpForwarding`
saga). Both are host/infra knowledge, not application code, so they're kept
out of this skill doc's own body.

Session naming and the mower query passed to `track` both use just the
**model prefix** of each mower's name - `${name%% *}` in bash, e.g. "AM430X"
out of "AM430X NERA" - relying on the CLI's existing name-contains matching
(`MowerService.FindMower`) to resolve that shortened form back to the full
mower (confirmed live: `am.sh status AM430X` correctly resolves to "AM430X
NERA"). Deliberately not the full name: avoids a space ever reaching a tmux
argument at all (no quoting workaround needed - an earlier version built
each command as a `printf '%q '`-quoted string specifically to survive
`AM430X NERA`'s space through tmux's `/bin/sh -c`; no longer needed once the
argument itself has no space). **Only safe while each model prefix is
unique across the account** (true for the current 3, one of each model) -
if a second mower ever shared a prefix, this would need to fall back to the
full name or the mower id instead, noted directly in `startall.sh`'s own
comments too.

**Not tested against real tmux** - tmux isn't installed on the Windows dev
machine (confirmed: `which tmux` fails, `tmux -V` too), so the tmux
orchestration itself (`new-session`, `send-keys`, `kill-session`) was
written from documented tmux behavior and verified only where possible
without tmux (mower-name/short-name extraction checked directly against the
real `.data/mowers.json`, both scripts `bash -n`-checked for syntax, and the
shortened-name resolution confirmed via a real `am.sh status AM430X` call -
just not the tmux session lifecycle itself). This gap turned out to matter:
the first real run on the Debian container only reliably started 1 of 3
sessions per invocation (had to run `startall.sh` twice to get all 3 up).

**Root cause, confirmed by reasoning through the symptom (not directly
reproduced, since no tmux here) - concurrent `dotnet build` races.**
`tmux new-session -d` returns immediately without waiting for the launched
command, so the original version's tight loop (one `tmux new-session ...
am.sh track $short` per mower) could fire off up to 3 concurrent `am.sh`
invocations - and `am.sh` runs `dotnet build` on *every* call, unconditionally.
Concurrent builds against the same project's `obj`/`bin` output can race;
the loser fails, `am.sh`'s `set -e` stops before ever reaching `exec dotnet
... track`, and since that failed `am.sh` was the only process in its pane,
the tmux session self-closes almost immediately after being created - looks
exactly like "only one mower actually started" even though the script's own
`echo` claimed all three did (it only reports that the `tmux new-session`
*command itself* was issued, not that the process inside survived).

**Fix**: `startall.sh` now builds once, up front (`dotnet build
AutomowerConsole/AutomowerConsole.csproj`), and each tmux session runs the
built `.dll` directly (`dotnet "$dir/AutomowerConsole/bin/Debug/net10.0/AutomowerConsole.dll"
track "$short"`) instead of going through `am.sh` - eliminating the redundant
per-session rebuild entirely rather than just narrowing the race window.
Verified locally that the build-once step and running the `.dll` directly
both work correctly (`dotnet current` via the built dll succeeded).

**This fixed the build race but not the underlying symptom** - a real run on
the container still only kept 1 of 3 sessions up (`tmux ls` showed only
`automower-AM405X`, moments after `startall` reported starting all three).
Since the build race is eliminated, this has to be a different, still
unconfirmed failure - something in `track` itself throwing early for
2 of 3 mowers (unhandled exception, auth, a file-write collision on the
shared `.data/schedule.json`, ...). The structural problem is the same
either way: `tmux new-session -d ... dotnet "$dll" track "$short"` runs the
`.dll` as the pane's only process with nowhere for its output to go, so a
fast crash closes the pane before there's any chance to attach and see why
- "only one started" with no error message to explain the other two.

**Diagnostic infrastructure**: each session's stdout+stderr is redirected to
`.data/startall-<short>.log` (`bash -c "dotnet '$dll' track '$short' >
'$log' 2>&1"` in place of the bare `dotnet ... track` command), so a fast
crash is diagnosable after the fact via `cat .data/startall-AM430X.log`
instead of needing to reproduce it by running the same command in the
foreground by hand. Deliberately not also `tee`ing to the pane / keeping the
pane open after exit - that would change what a *clean* stop looks like too,
breaking `stopall.sh`'s "closes itself within 3s" detection for a graceful
Ctrl+C stop.

**Actual root cause, confirmed via that log**: Husqvarna's Authentication
API, not the app - `HttpRequestException: Token request failed (400
BadRequest): {"error":"invalid_request","error_description":"Simultaneous
logins detected for client[id=...], user[id=..., email=...]",
"error_code":"simultaneous.logins"}`. `tmux new-session -d` returns
immediately without waiting for the launched command, so the original
no-delay loop fired all 3 mowers' `AuthenticateAsync()` calls (each its own
OS process, same app key/secret) within milliseconds of each other -
Husqvarna's auth service treats that as suspicious concurrent logins for one
client id and rejects all but one. Same "only 1 of 3 survives" symptom as
the build race, completely different cause - the earlier build-once fix was
real and necessary, just not sufficient on its own.

**Fix**: `startall.sh` now staggers session starts with a 5s `sleep` between
each one actually launched (skipped for a mower whose session already
exists, so re-running against a partially-up set doesn't wait needlessly) -
comfortably longer than a single OAuth2 token round-trip, so each
`AuthenticateAsync()` call completes before the next one fires. Not yet
re-verified against a real container run.

**Repo layout**: the console app lives in `AutomowerConsole/` (its own
subfolder), with `automower.slnx` (solution file) plus `am.cmd`/`am.sh` at
the true repo root. Moved there specifically to fix a bare `dotnet build`
failing with "more than one project or solution file" once `automower.slnx`
appeared alongside the `.csproj` at the repo root. `migrate-to-dotfolders.sh`
(which handled moving an even older checkout's `bin/`-based config/data into
`.config/`/`.data/`) was deleted as no-longer-needed once this move happened.

**`AutomowerConsole.Tests/`** (sibling folder, NUnit, referenced by
`automower.slnx`) - scaffolded empty at first (deferred twice: once when the
`AutomowerConsole/` move was planned, once when the test project itself was
created), now has its first real content:
`TrackingServiceTests.cs` (`TrackingServiceAggregateDailyActivityTests`, 4
tests, all passing) exercises `TrackingService.AggregateDailyActivity` - a
pure, static method (deliberately extracted from `SummarizeDailyActivity`
for exactly this reason: testable without a real track log file) - against
a real 3-day `sessions` history from AM430X NERA (2026-07-21 through
2026-07-23), reproduced as `TrackSession` fixture data rather than raw
JSONL polls, since `sessions` output is what the user actually had on hand.
One test specifically locks in the overnight-session/midnight-attribution
behavior (`OvernightChargingSessionCountsEntirelyTowardItsStartDay`) using
a real cross-midnight session from that history. Originally reached
`TrackingService`/`TrackSession` (then `internal` in `AutomowerConsole`) via
`<InternalsVisibleTo Include="AutomowerConsole.Tests" />` - superseded by
the `AutomowerConsole.Core` extraction (see top of this doc), where
`TrackingService` and friends are genuinely `public` now that a second real
consumer (`AutomowerWeb`) needs them too; the tests project just references
`AutomowerConsole.Core` directly, no `InternalsVisibleTo` needed for this
particular class anymore (Core still grants it to
`AutomowerConsole.Tests` for the handful of types that stayed `internal`).
Packages were bumped to latest via `dotnet outdated -u` right after
scaffolding (NUnit 4.3.2→4.6.1, NUnit3TestAdapter 5.0.0→6.2.0,
Microsoft.NET.Test.Sdk 17.14.0→18.8.1, coverlet.collector 6.0.4→10.0.1,
NUnit.Analyzers 4.7.0→4.14.0, as of 2026-07-22) - re-run that whenever
picking this back up if it's been a while, rather than assuming these stay
current.

**Domain nuance surfaced by the user while reviewing real session data,
worth remembering even though no code changed**: the API's `activity` label
for "at the charger" is not a reliable signal for whether real charging
happened. A `PARKED_IN_CS` session with flat battery can still represent
real charging time that only becomes visible via a much-higher battery% on
the *next* session. After walking through this, the user's own conclusion
was explicitly **not** to attempt battery-delta-based accuracy - keep
`CHARGING`/`PARKED_IN_CS` summed as one combined "at the charger" total
(`IsAtCharger`), as `AggregateDailyActivity` already did. If this comes up
again, that conclusion was reached, not skipped.

**`track` used to only log the arrival poll and skip repeats while still
parked at the charger (changed 2026-07-27) - now every poll is logged,
same as any other activity.** The old skip logic could leave a real gap:
if the mower's own `activity` label flipped from `CHARGING` to
`PARKED_IN_CS` on the same poll that crossed 100%, that poll started the
*next* session instead of ending the charging one, leaving the charging
session's `BatteryEnd` stuck at whatever it was on arrival - confirmed
against a real case (2026-07-27, AM308V: a `Charging` session logged only
`26%→26%` with a 1h43m gap before the next, unrelated `Parked` poll at
`100%`). Logging every poll makes new gaps like that far less likely (the
largest possible miss is now one poll interval, not up to ~30 minutes at
night) but doesn't eliminate the label-flip-on-the-same-poll case entirely.
`SummarizeSessions` now calls `TrackingService.BackfillChargingEndBattery`
as a display-layer correction: when a charging-type session's `End` exactly
equals the next at-charger session's `Start` (no gap) and that next
session's `BatteryStart` is higher, the first session's `BatteryEnd` is
backfilled from it. Doesn't touch `AggregateDailyActivity`/
`AggregateMonthlyActivity` totals at all (those derive duration from
`Start`/`End`/`ChargeCompleteAt`, never from `BatteryEnd`) - purely a
session-list display fix, applies to both the CLI's `sessions` command and
`AutomowerWeb`'s session table.

The user explicitly decided against a "poll faster near 100%" optimization
(diminishing value once the two fixes above are in) and separately flagged
that `track-*.jsonl` log truncation/retention is worth doing eventually,
but deferred - logging every poll now (rather than skipping most of the
charger dwell time) makes these logs grow faster than before, so this is
worth prioritizing sooner than "eventually" once file sizes become
noticeable.

**`am.cmd`/`am.sh` build once then launch `bin/Debug/net10.0/AutomowerConsole.dll`
directly - deliberately not `dotnet run`.** Confirmed by direct incident on
the Debian container: a `track` session started via `dotnet run` could not be
stopped with Ctrl+C or `kill -INT <pid>` (nor `pkill -INT -f`); only
`kill -9` worked. Root cause: `dotnet run` is a build-and-launch wrapper
process, and it does not reliably forward POSIX signals to the child process
it spawns - the `Console.CancelKeyPress` handler in `CommandTrack` is correct
and was never the problem, the signal just never reached it. Never recommend
`dotnet run` for anything long-running (`track` in particular); always the
built binary via the shortcut scripts. Log data itself was never at risk
either way, since `track` flushes each poll to disk immediately - the only
casualty of the SIGKILL workaround was the graceful stop summary line.

## Project layout

Four-layer split: `Program.cs` (CLI dispatch + printing, plain eager locals
for each service - see below) → four service classes (data-gathering/
calculation, allowed to print their own intrinsic diagnostics) →
`AutomowerConnect` (auth-handling facade, reached via a static singleton
`AutomowerConnect.Instance`, never referenced by `Program.cs` at any
distance) → `HusqvarnaClient` (raw HTTP). A dedicated output-formatting
class (splitting the "printing" half out of `Program.cs`) is a known,
explicitly deferred next step - not done yet.

- `AutomowerConsole/Program.cs` — CLI dispatch (top-level statements + local
  functions) + result formatting/printing; ~594 lines (was ~970 before the
  service-layer work). Constructs `mowerService`/`mowerDetailService`/
  `scheduleService`/`trackingService` as plain eager local variables right
  after `command`/`rest` - **not** lazy `GetXService()` accessor methods
  (an earlier pass had those; removed once construction became parameterless
  and side-effect-free, since a "cache a value in a nullable field, expose
  via a method" wrapper adds nothing once there's no laziness left to do -
  see `AutomowerConnect.Instance` below for why they're still safe to
  construct unconditionally on every run, including `help`/`config`)
- `AutomowerConsole/MowerService.cs` — mower listing/caching/resolution
  (`RefreshMowersAsync`, `EnsureMowersAsync`, `FindMower`,
  `ResolveExplicitMowerAsync`, `ResolveMowerAsync`). Prints its own
  ambiguous-match/not-found/fetching-from-API diagnostics — accepted by
  design, since they're intrinsic to the resolution process itself, not a
  separate presentation layer. `CommandList` was found duplicating
  `RefreshMowersAsync`'s logic inline instead of calling it (a bug from the
  pass that created the method - built it, never wired the one caller that
  should have used it) - fixed as part of this pass, worth remembering as a
  reminder to grep for a new method's expected call sites after adding it,
  not just confirm it compiles
- `AutomowerConsole/MowerDetailService.cs` — a specific, already-resolved
  mower's live detail data: `GetMowerDetailAsync` (wraps `GetMowerAsync`;
  used by `status`, `workareas`, `workarea`, `stayoutzones`, `schedule` -
  five different commands projecting different slices of the same response),
  `GetMowerRawAsync` (`status --all`), `GetMessagesAsync`, `GetWorkAreaDetailAsync`.
  All one-line forwards to `AutomowerConnect.Instance` - added anyway per
  explicit direction, since the point was architectural (no direct
  `Program.cs` → `AutomowerConnect` reference at all, regardless of how thin
  the wrapper is), not about extracting non-trivial logic
- `AutomowerConsole/ScheduleService.cs` — calendar/schedule calculations +
  `schedule.json` cache (`DayFlag`, `IsWithinSchedule`, `NextCalendarStart`,
  `SaveScheduleForMower`, `GetCachedTasks`, `DetermineTrackingInterval`).
  Pure calculation + `Storage`, no `AutomowerConnect` dependency. Calls the
  pre-existing `DateTimeOffset.IsNighttime(...)` extension (`Extensions.cs`,
  the user's own earlier edit, deliberately left as-is rather than absorbed)
- `AutomowerConsole/TrackingService.cs` — reads/writes `track-<mower>.jsonl`:
  `RunAsync` (the live poll loop, including its per-iteration progress
  prints — same accepted-Console-output justification as `MowerService`;
  resolves `AutomowerConnect.Instance` as a *local* inside the method body,
  not a constructor-cached field, so constructing `TrackingService` itself
  stays side-effect-free) and `SummarizeSessions` (log parsing + session
  grouping, returns `List<TrackSession>` data — `Program.cs` still does that
  command's line formatting, per "output stays in `Program.cs` for now").
  Also owns `IsAtCharger` (static, used by both methods and by `Program.cs`'s
  `CommandSessions` formatting)
- `AutomowerConsole/AutomowerConnect.cs` — facade over `HusqvarnaClient`
  owning the auth lifecycle (auto-authenticate on first use, retry once on
  `HttpRequestException`). Reached exclusively via the static
  `AutomowerConnect.Instance` (`_instance ??= Create()`, where `Create()`
  calls `Storage.LoadConfig()`) - the four services above call this, `Program.cs`
  never does, not even to construct it (that responsibility used to live in
  `Program.cs`'s `GetConnect()`, deliberately moved). **Must stay lazy**:
  `Storage.LoadConfig()` throws if `AppKey`/`AppSecret` aren't set yet, and
  `help`/`config`/`errorcodes`/`current` must keep working with zero
  `config.json` present - verified directly (temporarily renamed
  `.config/config.json` away, confirmed `am help` still succeeds) rather
  than just asserted. This is why none of the four services' constructors
  may touch `AutomowerConnect.Instance` themselves, only method bodies that
  actually make a call. Constructor kept `internal` (not `private`) rather
  than folded away, so a future test can construct an independent instance
  against test credentials without going through the shared singleton.
- `AutomowerConsole/HusqvarnaClient.cs` — low-level auth + HTTP calls
- `AutomowerConsole/Models.cs` — JSON DTOs
- `AutomowerConsole/Storage.cs` — reads/writes `.config/config.json` and `.data/*.json(l)`
- `AutomowerConsole/ErrorCodes.cs` — full error code → description table
- `AutomowerConsole/Extensions.cs` — `DateTimeOffset.IsNighttime(...)`, a C#
  14 extension member (the user's own edit, predates the service-layer work)

**Testability note**: none of `MowerService`/`MowerDetailService`/
`TrackingService` are currently unit-testable in isolation from the real
network - `AutomowerConnect` has no interface and isn't virtual, so a fake
can't be substituted regardless of whether it's constructor-injected or
reached via `.Instance`. Moving to the static singleton didn't give up
anything actually exercised; if isolated unit tests need this later, the
follow-up is extracting an `IAutomowerConnect` interface, not reverting the
singleton.

**Known, deliberately deferred**: `CommandWorkArea`'s work-area matcher
(index/exact-id/exact-name/unique-contains) is structurally identical to
`MowerService.FindMower` (same shape, different type) - a good candidate to
unify later, explicitly not done during the service-layer extraction since
it wasn't asked for. `CommandConfig` (reflection-based `Key=Value` setter)
was also left untouched - small and self-contained enough not to be part of
"Program.cs is too big."

- `.config/config.json` — app key/secret + `track` interval settings; gitignored,
  holds a live secret. `config.example.json` (repo root, tracked) is the
  placeholder template.
- `.data/mowers.json` / `.data/state.json` / `.data/schedule.json` — generated
  at runtime (mower cache, active selection, cached per-mower calendar); also
  gitignored
- `.data/track-<sanitized mower name>.jsonl` — one append-only track log per
  mower (see `Storage.GetTrackLogPath`/`SanitizeForFileName`), not a combined
  log — deliberate, per explicit user request ("no need to see anything on
  them together at all")

**Both `.config/` and `.data/` are anchored to the repo root, not `bin/`.**
`Storage.FindRepoRoot()` walks up from `AppContext.BaseDirectory` (which is
`AutomowerConsole/bin/<Config>/<TFM>/`) looking for the nearest `*.slnx`,
falling back to `AppContext.BaseDirectory` itself if none is found (e.g. a
bare publish without source alongside it). It looks for `*.slnx` rather than
`*.csproj` specifically because the `.csproj` now sits one level down in
`AutomowerConsole/` - anchoring to it would land one level too shallow.
There's only ever one `.slnx`, and it lives in the true repo root by
construction (verified after the move: `.config/config.json`'s `AppKey`/
`AppSecret` printed identical masked values before and after, proving the
lookup landed on the exact same pre-existing files, not a fresh/relocated
copy). This exists specifically because `dotnet clean`
deletes everything MSBuild tracked as build output — verified empirically:
before this fix, `config.json` lived in `bin/` via a `CopyToOutputDirectory`
csproj item, and a `clean` silently deleted it, then the next build
re-copied a stale version from a since-removed repo-root source file,
discarding any edits made only via `config` at runtime. `mowers.json` etc.
were never csproj-tracked so `clean` never touched *them* even under the old
layout, but they'd been sitting in the equally fragile `bin/` location
anyway. Moving everything to `.config/`/`.data/` makes all of it immune to
`clean`/rebuild by construction, not just the files that happened to
survive before.

Commands: `config`, `config Key=Value ...`, `list`, `use <name|id|index>`,
`current`, `status [--all]`, `messages`, `errorcodes`, `workareas`,
`workarea <name|id>`, `stayoutzones`, `schedule [mower]`,
`track [seconds] [mower]`.

`config` with no args prints current values (AppKey/AppSecret masked to
first-4...last-4 chars); `config Key=Value ...` sets one or more fields via
reflection over the `Config` record in `Models.cs` (see `CommandConfig`) —
any new field added to `Config` is automatically settable this way, no CLI
wiring needed. It's also how `.config/config.json` gets created in the first
place (`Storage.LoadConfigForEditing()`, unlike `Storage.LoadConfig()`,
doesn't require AppKey/AppSecret to already be set).

**Security history**: `config.json` with real credentials was accidentally
committed in this repo's first commit (back when it lived at the repo root).
Fixed by deleting the branch ref (`git update-ref -d refs/heads/main` — safe
since it was the only commit, no remote existed yet) rather than just
`git rm --cached`, so the secret never existed in any reachable git history,
not just future commits. If this ever happens again *after* a remote exists,
deleting the ref won't be enough — history rewrite (or credential rotation)
would be needed instead.

All mower-scoped commands (`status`, `messages`, `workareas`, `stayoutzones`,
`schedule`, the trailing arg on `workarea <name|id>`, and the mower arg on
`track`) accept an optional trailing `[mower]` override (name/id/list index)
to target a different mower for one call without changing the persisted
active selection in `.data/state.json`. This goes through a shared
`ResolveMowerAsync` helper in `Program.cs`.

### `track` — adaptive-interval polling with logging

Polls `GET /mowers/{id}` and appends one JSON line per kept poll to
`.data/track-<mower name>.jsonl` (one file per mower, e.g.
`.data/track-AM430X-NERA.jsonl`): `{timestamp, mowerId, mowerName, bytes,
response}` where `response` is the full raw mower payload. Built to answer
"how much data would a day of polling actually be" empirically instead of
estimating — a log file's size on disk *is* the answer, per mower.

Every poll is logged, including while the mower's `activity` is `CHARGING`
or `PARKED_IN_CS` (see `IsAtCharger`) - changed 2026-07-27; before that,
only two polls per charger stay were logged (arrival, and the first poll
where `batteryPercent` reached 100), skipping everything else to save log
volume. That skip logic could leave a real gap in a charger session's own
data: if the mower's own `activity` label flipped from `CHARGING` to
`PARKED_IN_CS` on the exact poll that crossed 100%, that poll started the
*next* session instead of ending this one, leaving `BatteryEnd` stuck at
whatever it was on arrival - confirmed against a real case (2026-07-27,
AM308V: a `Charging` session logged only `26%→26%` with a 1h43m gap before
an unrelated `Parked` poll at `100%`). Logging every poll makes new gaps
like that far less likely (the largest possible miss is now one poll
interval instead of up to ~30 minutes at night) but doesn't eliminate the
same-poll label-flip case entirely - see `TrackingService
.BackfillChargingEndBattery`, a `SummarizeSessions`-level display
correction that backfills a charging session's `BatteryEnd` from the next
contiguous at-charger session's `BatteryStart` when they connect with no
gap. Log volume grows faster now than under the old skip logic (a charger
stay no longer collapses to ~1-2 lines) - `track-*.jsonl` truncation/
retention is worth prioritizing sooner rather than later as a result.

Interval is chosen fresh every poll, in this priority order (see
`CommandTrack`, `IsWithinSchedule`, `IsNighttime`):

1. **Active or in a scheduled window** → `ScheduledIntervalSeconds` (config
   default 60s; the CLI's `track [seconds]` arg overrides just this one).
   Covers both "mower is actually out and about" (`activity` not
   `CHARGING`/`PARKED_IN_CS`) and "we're inside a calendar task's time
   window but the mower might still be charging" — the latter exists
   because charge duration is unpredictable, so we poll fast to catch the
   exact moment it leaves rather than waiting up to the idle interval.
2. **Nighttime** (default 22:00–08:00, config `NightStartHour`/`NightEndHour`,
   wraps past midnight) and not scheduled/active →
   `NightIntervalSeconds` (config default 1800s = 30 min). Rationale: no
   manual mowing start is expected overnight.
3. **Otherwise** (daytime, not scheduled, not active) →
   `IdleIntervalSeconds` (config default 300s = 5 min) — watching for a
   manually-started mow. If one starts, the *next* poll already sees
   `activity` off the charger and self-upgrades to the fast interval, so
   detection lag is at most one idle interval.

The schedule used for decision #1 comes from `.data/schedule.json`, refreshed
for free from every `track` poll's own `attributes.calendar` (the mower
payload already includes it — no extra API call). It's a dict keyed by
mower id: `{mowerId: {MowerName, FetchedAt, Tasks}}`, where `Tasks` is the
same `CalendarTask[]` shape used by `workarea`'s per-area schedule, but with
`workAreaId` populated per task (confirmed present on the mower-level
`GET /mowers/{id}` calendar too, not just the per-work-area endpoint). Run
`schedule [mower]` standalone to force a refresh/inspect it without starting
`track` (e.g. right after changing the schedule in the app).

### `sessions` — summarizing a track log

`sessions [--calendar] [mower]` (`CommandSessions`) reads `track-<mower>.jsonl`
directly (no API call unless resolving the mower name from `[mower]` needs
the cached-mowers fallback) and groups consecutive polls sharing the same
`(activity, workAreaId)` pair into sessions - split on **either** changing,
not just activity, since a mower can go straight from one work area into the
next while `activity` stays `MOWING` the whole time (verified with a
synthetic two-area sequence: correctly split into two sessions despite
identical activity throughout). One line per session: date, start-end time,
duration (`FormatDuration`, e.g. `1h32m`/`45m`), battery start%→end%
(`DescribeActivity` maps the raw activity enum to a friendly label), and a
`[work area name]` suffix resolved from that poll's own `workAreas[]` list
(same shape as the mower-level `workAreas` embedded in `GET /mowers/{id}`;
names are cached in a `Dictionary<long, string>` built while scanning, since
not every line necessarily carries a fresh copy). Omitted when the id has no
resolvable/non-empty name (e.g. `workAreaId: 0` with an empty-named default
area, as seen on this account's AM405X, which has no named work areas
configured). Purely local file parsing via `Storage.GetTrackLogPath(mowerName)`
- `ResolveMowerAsync` is only used to turn a `[mower]` arg (or the active
mower) into a name, not to call the API.

**Session end = the next differing poll's timestamp, not the session's own
last poll.** This is deliberate: a session's own last recorded poll is
still mid-state (whatever changed hadn't happened yet), so using its own
timestamp as both start and end would show a nonsensical 0-duration
session even for a session with several polls in it. Using the next poll's
timestamp instead (whatever
activity/work area that turns out to be) is the earliest point the log can
actually confirm the state changed, at the cost of some imprecision bounded
by whatever interval was active at the time (up to `IdleIntervalSeconds` or
`NightIntervalSeconds`). The last session in the file has no "next" poll, so
it prints `ongoing` and computes duration to `DateTimeOffset.Now` instead.

Verified against synthetic datasets (overnight charging session spanning
midnight, single-line Parked session between two Leaving/Mowing sessions,
trailing ongoing Mowing session, and a two-work-area Mowing sequence that
split correctly despite constant activity) before confirming against real
(if sparse) local data - all cases behaved correctly.

**Print order is newest-first**, but grouping still has to scan
oldest-to-newest internally (each session's end depends on the *next* point
chronologically) - `points` is sorted ascending as before, session strings
are accumulated into a `lines` list in that same ascending order, then
printed by iterating `lines` backwards. Don't be tempted to reverse `points`
itself before grouping; that would break the "next differing poll" lookahead
the whole end-time calculation depends on.

**Charger sessions also get a charge-complete marker**, independent of
`--calendar`: `full at HH:mm` if `TrackSession.ChargeCompleteAt` is set (the
first poll within that session where `batteryPercent` hit 100 - see
`track`'s two-points-per-stay logging above), `still charging` if the
session is still ongoing (`End is null`) and hasn't reached 100% yet, or no
marker at all if the session already ended with no `ChargeCompleteAt` -
deliberately not labeled "never reached 100%", since that's indistinguishable
from an old log line predating this field (single-point charger sessions
from before this existed also report `ChargeCompleteAt: null`).

**`--calendar`** appends two values to the end of `Charging`/`Parked` session
lines only (`IsAtCharger(activity)`) - same line, not a second line (kept
single-line deliberately for wide-terminal use), both sourced from data
already embedded in that session's *own* historical poll(s), so still zero
extra API calls:
- "next calendar start" — `NextCalendarStart(tasks, sessionStart)`, a new
  helper next to `IsWithinSchedule`/`IsNighttime` that scans up to 8 days
  forward for the earliest task start strictly after a given reference time.
  `tasks` here is `latestCalendarTasks`, updated while scanning the log
  whenever a line's `attributes.calendar` deserializes to a non-empty
  `CalendarInfo` (mirrors the `workAreaNames` caching pattern already used
  for work area labels).
- "next planned start" — read directly from that session's first poll's own
  `attributes.planner.nextStartTimestamp` (captured per-point as
  `PlannerNextStart` when parsing `points`), **not** recomputed live - it's
  a historical snapshot of what the mower's planner believed at the time,
  which is deliberate: the two Parked sessions in real test data showed
  different "next planned start" values (16:03 vs 11:00) despite an
  unchanged calendar, demonstrating the planner's live decision-making
  really does move independently of the static schedule.

### `daily` — per-calendar-day activity rollup

`daily [mower]` (`CommandDaily`) calls
`TrackingService.SummarizeDailyActivity(mowerName)`, which itself calls
`SummarizeSessions(mowerName, includeCalendarInfo: false)` and re-aggregates
the returned `List<TrackSession>` by `DateOnly.FromDateTime(s.Start.Date)` -
no separate log-parsing pass, fully reuses the existing session boundaries.
Three buckets per day: Mowing, summed per work area
(`DailyAccumulator.AddMowing` finds-or-creates by `WorkAreaName` including
`null`, so an unresolved-name area still gets its own bucket rather than
merging into whichever other unnamed area happened first) via a private
nested `DailyAccumulator` class; and Charging/Parked, split from what used
to be one combined `TrackingService.IsAtCharger` total (`CHARGING` +
`PARKED_IN_CS` together - still no finer split on *that* axis, the
activity label itself remains an unreliable signal). The split instead uses
`TrackSession.ChargeCompleteAt`: Charging is session-start → that point (or
the whole session if `ChargeCompleteAt` is null - "still charging" as far
as the data shows, same treatment for a genuinely-ongoing session and for
an old log line that predates this field), Parked is that point → session
end (charged but not actively charging anymore). `Parked` is omitted from a
day's `daily` line entirely when zero (e.g. every charger session that day
is still `Charging`-only), same convention already used for Charging.
Named `Parked` rather than the earlier `Full` (renamed after it read oddly
as a table column header in `AutomowerWeb` - see **Web dashboard** in
`README.md`) - same field, same meaning, just a clearer name for the same
"charged but sitting there, not mowing" concept. Sessions in neither bucket
(`GOING_HOME`,
`LEAVING`, `STOPPED_IN_GARDEN`, ...) are silently dropped from the rollup -
not an oversight, just outside what was requested. Days that end up with no
bucket populated (e.g. the only session that day was a 5-minute `Leaving`)
are filtered out of the result rather than printed as a blank line.

**Whole session → start day, no midnight-splitting** - same simplification
`SummarizeSessions` already makes for its own single date column, carried
through rather than added on top. Confirmed via live data this produces a
Charging total **exceeding 24h** for an in-progress `Parked` session that
started the afternoon before and is still ongoing as of "now" (real:
`24h35m` shown under the start day) - not a bug, just what "count the whole
session under its start day" necessarily means once a session has run past
midnight. Flagged to the user directly rather than silently shipped, since
at a glance it reads as broken. Verified against a synthetic 2-day, 2-work-area
dataset that the actually-requested behavior is correct: same-day, same-area
mowing sessions sum together (two visits to "Front Lawn" totaling 50m,
verified by hand against the source timestamps) rather than appearing as
duplicate line entries.

### `calendar` vs `planner`

Two distinct pieces of the mower payload, easy to conflate:

- `attributes.calendar.tasks[]` — static, user-configured recurring
  schedule. What `workarea`/`schedule` display; what `NextCalendarStart`
  computes from.
- `attributes.planner` — `{nextStartTimestamp, override, restrictedReason}`,
  the mower's live/computed next-action state, derived from the calendar
  plus real-time factors (battery, restrictions, overrides). Can diverge
  from a naive calendar lookup - confirmed via real data where "next
  planned start" (11:00 or 16:03) differed from "next calendar start"
  (09:00) on the same day. `override` is not currently modeled in
  `PlannerInfo` (`Models.cs`) or displayed anywhere - only
  `NextStartTimestamp` and `RestrictedReason` are, since that's all that's
  been asked for so far.

`schedule [mower]` (`CommandSchedule`) shows both together: the calendar
tasks (as before), then "Next calendar start" / "Next planned start" (via
the same `NextCalendarStart` helper plus live `mower.Attributes.Planner`),
and `RestrictedReason` when it's not `NOT_APPLICABLE`/empty - confirmed
`WEEK_SCHEDULE` as a real observed value on this account, previously unseen
in any earlier session.

## Authentication

OAuth2 client-credentials grant against the **Authentication API** (separate
host from the mower API):

```
POST https://api.authentication.husqvarnagroup.dev/v1/oauth2/token
Content-Type: application/x-www-form-urlencoded

grant_type=client_credentials&client_id={appKey}&client_secret={appSecret}
```

Response: `access_token`, `token_type`, `expires_in`, `provider`.

Every call to the Automower Connect API then needs three headers:

```
Authorization: Bearer {access_token}
Authorization-Provider: husqvarna
X-Api-Key: {appKey}
```

Base URL: `https://api.amc.husqvarna.dev/v1`

## Endpoints used

- `GET /mowers` — list, `{ "data": [ {id, type, attributes: {...}} ] }`
- `GET /mowers/{id}` — single mower, `{ "data": {id, type, attributes: {...}} }`
  — **note: `data` is a single object here, not an array**, unlike the list endpoint.
- `GET /mowers/{id}/messages` — `{ "data": { "type": "messages", "id": "messages",
  "attributes": { "messages": [ {time, code, severity, latitude?, longitude?} ] } } }`
  — also a single wrapped object, **not** a JSON:API array of message resources
  the way the naming might suggest.
- `GET /mowers/{id}/workAreas/{workAreaId}` — single work area detail,
  `{ "data": { "type": "workArea", "id": "<workAreaId as string>", "attributes":
  {workAreaId, name, type, cuttingHeight, enabled, lastTimeAbandoned,
  useGlobalCuttingHeight, calendar: {tasks: [...]}} } }`. Same fields as the
  `workAreas[]` entries embedded in `GET /mowers/{id}`, **plus** a
  `calendar.tasks` schedule scoped to that one work area (start/duration in
  minutes, weekday booleans) that the embedded list entries don't carry.

## Gotchas / non-obvious facts

- **No weather/rain, network type (wifi/mobile), signal strength, "smart
  routine", or product-number field exists anywhere in `GET /mowers/{id}`**
  - confirmed by inspecting a complete real raw response (`status --all`,
  2026-07-25) from this account's most full-featured mower (headlights,
  work areas, stay-out zones capabilities all `true`). If asked to surface
  any of these in `AutomowerWeb`, don't guess a field name - re-check a
  real raw dump first; it wasn't there before and there's no reason to
  assume a later dump would differ. What *is* real and modeled (`Models.cs`,
  `AutomowerConsole.Core`): `capabilities` (feature flags),
  `settings.cuttingHeight`/`settings.headlight.mode`, `statistics` (lifetime
  counters - all `*Time` fields in **seconds**, `totalDriveDistance` in
  **meters**, per aioautomower's `model_statistics.py` docstrings - verified
  against real values, e.g. `upTime: 31083938` → 359 days, matching the
  account's actual mower age), `battery.remainingChargingTime` (seconds),
  and `planner.override.action` (`NOT_ACTIVE`/`FORCE_PARK`/`FORCE_MOW`). All
  surfaced in `AutomowerWeb`'s `/mower/{name}` page - status facts at the
  top, Settings & capabilities and Operation (lifetime) sections at the
  bottom.
- **Two different, easily-conflated "cuttingHeight" fields, on two different
  scales** - `settings.cuttingHeight` (global, `SettingsInfo` in
  `Models.cs`) is a **1-9 dial value**; `workAreas[].cuttingHeight`
  (per-area, `WorkArea.CuttingHeight`) is a **percentage (0-100)** of the
  mower model's adjustable blade-height range - confirmed against Home
  Assistant's `husqvarna_automower` integration (`number.py`'s
  `native_unit_of_measurement: PERCENTAGE` for the work-area one, plain
  1-9 min/max for the global one). The Husqvarna app shows the per-area one
  converted to cm - that per-model min/max range isn't exposed anywhere in
  this API, so it can't be derived from the API response alone.
  `AutomowerWeb/CuttingHeightEstimator.cs` estimates it anyway, using a
  linear-interpolation formula and per-model min/max table from an
  **unverified, third-party explanation** (no official Husqvarna citation):
  `cm = min + percentage/100 * (max - min)`. Confirmed to match the real
  app's own displayed value for this account's AM430X ("oversiden": API
  `87` → estimate `5.5 cm`, matching the app exactly) - one confirmed data
  point, not a guarantee for other models/values. The min/max/electronic
  table lives in `AutomowerWeb/cutting-height-ranges.json` (tracked in git,
  copied to the build output - **not** `.config/`, which is blanket-
  gitignored for secrets and wrong for non-secret reference data like
  this), specifically so a new mower model can be added by editing that
  file, no code change/rebuild needed if edited directly in the output
  directory. A model with **manual** (knob) height adjustment - e.g. the
  308V - can't report its real height at all; the API just returns a
  meaningless placeholder (observed: always `0`) instead, so
  `"electronic": false` in that file skips the conversion entirely rather
  than showing a plausible-but-wrong number.
- **`MapStaticAssets()` was silently broken in this preview SDK's
  Production mode** - not an `AutomowerWeb`-specific bug, a framework-level
  one. Confirmed by direct testing (2026-07-25): every static asset,
  including `_framework/blazor.web.js` itself (not just `app.css`), came
  back as a `200 OK` with `Content-Length: 0` in Production - meaning the
  app would have been completely non-interactive (no Blazor Server circuit
  can establish without its own JS loading), not just unstyled. This
  reproduced identically from a full `dotnet publish` output with the
  compressed/manifest assets genuinely present on disk - it was never a
  build-vs-publish problem, ruling out the fix originally assumed (see
  `startweb.sh`'s git history for that now-superseded reasoning). Fixed by
  dropping `MapStaticAssets()`/`@Assets[...]` entirely in favor of the
  classic `UseStaticFiles()` middleware + plain literal asset paths
  (`Program.cs`, `App.razor`, `ReconnectModal.razor`) - a manual `?v=` query
  string (`AppInfo.Version`) substitutes for the cache-busting the
  fingerprinted filenames used to give for free. That swap uncovered two
  more real issues, both now fixed: `UseStaticFiles()` resolves `wwwroot`
  relative to the app's **content root**, which defaults to the *launching
  shell's current directory* (not the app's own location) unless set
  explicitly - `Program.cs` now anchors it to `AppContext.BaseDirectory`.
  And a plain `dotnet build` output never physically copies `wwwroot` at
  all (ASP.NET Core's static web assets are manifest-referenced back into
  the *source* tree for a build, which only `MapStaticAssets()` ever
  understood) - `dotnet publish` is what actually produces the physical
  copy, confirmed to happen under `-c Debug` too, not just `-c Release`, so
  `startweb.dev` (the local/LAN Development-mode alternative to
  `startweb.sh`) publishes too now, just faster (`-c Debug`) and to its own
  output directory.
- **The app trusts the host's system-local clock everywhere, uniformly** —
  `DateTimeOffset.Now` (`TrackingService.cs`'s poll timestamps,
  `ScheduleService.NextCalendarStart`'s day-boundary math) and
  `.ToLocalTime()` (every displayed timestamp in `Program.cs`: messages,
  status, workarea `lastTimeAbandoned`, planner `nextStartTimestamp`) — there
  is no explicit timezone handling anywhere in the codebase, not a
  mix of compensated/uncompensated spots. This works correctly only because
  the host's OS timezone is assumed to equal the mowers' own configured
  local timezone (Europe/Oslo) — which the calendar's `start`/`duration`
  minutes-from-midnight are implicitly defined in, regardless of what
  timezone the polling host happens to run. Confirmed as the root cause of a
  real ~2h discrepancy between `sessions`/`schedule` output and actual mower
  behavior on the QNAP container, whose OS clock defaulted to UTC (`date`
  showed `UTC`, no `/etc/timezone` file) — fixed by setting the container's
  timezone to Europe/Oslo (now part of `bootstrap.sh`), not by touching the
  code. Don't "fix" an apparent timestamp bug here without first checking
  `date` on whatever host is running it.
- **Inconsistent timestamp units in the same payload**: `messages[].time` is
  Unix epoch **seconds**. But `metadata.statusTimestamp`,
  `planner.nextStartTimestamp`, and `workAreas[].lastTimeAbandoned` are epoch
  **milliseconds**. Verified by cross-checking against the real clock — don't
  assume, check which field you're converting.
- `mower.errorCode` and `messages[].code` are bare integers (not strings) that
  index into the same error code table — see `ErrorCodes.cs`.
- `attributes.positions[]` (only visible via `status --all`, not modeled in
  the CLI) is a **GPS breadcrumb trail of actual mower movement, newest
  first**, capped at 50 entries (oldest dropped once full) — confirmed both
  from aioautomower's `model_positions.py` docstring and by observing exactly
  50 entries on all 3 of this account's mowers. It is **not** work-area
  boundary/geofence data — there's no per-point timestamp, so it gives you
  the recent path shape but not speed or dwell time. Work-area boundaries and
  the actual "My Lawn" map/mesh are not exposed by any endpoint in this API
  (see the map-backup discussion — Husqvarna's cloud map backups are not
  API-accessible either).
- `mower.inactiveReason: SEARCHING_FOR_SATELLITES` is a catch-all in the API
  and is **ambiguous** — user-reported experience is that it can mean actual
  GPS/satellite search, lost WiFi/4G connectivity, or a charging station
  problem. `Program.cs` surfaces this with an explicit caveat in `status`
  output; don't take the literal string at face value when diagnosing.
- **`activity: NOT_APPLICABLE` officially means "manual start required in
  mower"** (per the developer portal text below, supplied by the user
  2026-07-27) - corrects an earlier guess in this session that it was just
  transient noise during motion-start moments. Real track-log evidence
  (AM308V, 2026-07-27) shows it appearing for ~1 minute at a time between
  `LEAVING`/`MOWING` polls with mowing resuming on its own right after, with
  no sign of an actual physical button press - so the *literal* "needs a
  human at the mower" meaning doesn't fully square with what's observed
  either. Likely a brief placeholder value while the mower's own state
  machine is mid-transition and hasn't settled on its next real activity
  yet, rather than a genuine standing "come press the button" state every
  time it appears - but that's inference, not confirmed. Treat the official
  description as authoritative for what the *label* means, not as proof of
  what's happening every single time it's observed for just one poll.
- `capabilities.stayOutZones: true` only means the mower *supports* the
  feature — the `stayOutZones` attribute itself is `null` when no zones are
  configured (confirmed on all 3 of this account's mowers). When present, the
  shape (per aioautomower's model, not yet confirmed against a live response)
  is `{ "dirty": bool, "zones": [ {"id": str, "name": str, "enabled": bool} ] }`.
- The developer portal (`developer.husqvarnagroup.cloud/apis/automower-connect-api`)
  is a JS SPA — `WebFetch` only returns the page title, no usable content.
  Ground truth came from live `curl` calls against the real API instead.
- `mower.mode` (`MAIN_AREA`/`SECONDARY_AREA`/`HOME`/`DEMO`/`POI`/`UNKNOWN`) is
  **not** derived from `mower.workAreaId` and doesn't reliably tell you which
  work area the mower is in — confirmed against this account's own track
  logs, where `mode` stayed `MAIN_AREA` even while `workAreaId` pointed at a
  named, non-default custom work area ("oversiden"). aioautomower/Home
  Assistant's own integration treats them as two independent sensors (mode
  vs. work_area_id), not one derived from the other. `mower.workAreaId` (a
  field present in the raw API response but, until now, missing from
  `MowerActivityState` in `Models.cs`) is the field to resolve against
  `attributes.workAreas[]` for "what area is it actually in" — `status` now
  prints a resolved `Work area:` line from it instead of relying on `Mode`.

## Official `mode`/`activity`/`state` descriptions (Husqvarna developer portal)

The developer portal is a JS SPA that `WebFetch` can't render (see below) -
the user pasted this text directly from the site 2026-07-27, so it's
captured here verbatim as the authoritative source, superseding any
aioautomower-derived or inferred descriptions for these three fields.

**`mower.mode`**
- `MAIN_AREA` - Mower will mow until low battery. Go home and charge. Leave
  and continue mowing. Week schedule is used. Schedule can be overridden
  with forced park or forced mowing.
- `DEMO` - Same as main area, but shorter times. No blade operation.
- `SECONDARY_AREA` - Mower is in secondary area. Schedule is overridden
  with forced park or forced mowing. Mower will mow for requested time or
  until the battery runs out.
- `HOME` - Mower goes home and parks forever. Week schedule is not used.
  Cannot be overridden with forced mowing.
- `UNKNOWN` - Unknown mode.

**`mower.activity`**
- `UNKNOWN` - Unknown activity.
- `NOT_APPLICABLE` - Manual start required in mower (see the Gotchas note
  above - the literal meaning doesn't fully square with brief real-world
  occurrences between other activities).
- `MOWING` - Mower is mowing lawn. If in demo mode the blades are not in
  operation.
- `GOING_HOME` - Mower is going home to the charging station.
- `CHARGING` - Mower is charging in station due to low battery.
- `LEAVING` - Mower is leaving the charging station.
- `PARKED_IN_CS` - Mower is parked in charging station.
- `STOPPED_IN_GARDEN` - Mower has stopped. Needs manual action to resume.

**`mower.state`** (not currently modeled with friendly descriptions
anywhere - `status` just prints the raw value)
- `UNKNOWN` - Unknown state.
- `NOT_APPLICABLE` - (no description given by the source)
- `PAUSED` - Mower has been paused by user.
- `IN_OPERATION` - See value in `activity` for status.
- `WAIT_UPDATING` - Mower is downloading new firmware.
- `WAIT_POWER_UP` - Mower is performing power up tests.
- `RESTRICTED` - Mower can currently not mow due to week calendar, or
  override park - cross-reference `planner.restrictedReason`/
  `ExternalReasons.Describe(planner.externalReason)` for *why*.
- `OFF` - Mower is turned off.
- `STOPPED` - Mower is stopped, requires manual action.
- `ERROR`, `FATAL_ERROR`, `ERROR_AT_POWER_UP` - An error has occurred.
  Check `errorCode`. Mower requires manual action.

## External references

- Husqvarna Authentication API (token endpoint):
  `https://api.authentication.husqvarnagroup.dev/v1/oauth2/token`
- Husqvarna Automower Connect API (base):
  `https://api.amc.husqvarna.dev/v1`
- Developer portal, main page (JS SPA — `WebFetch` only returns the page
  title, not the content; open it in a real browser instead):
  https://developer.husqvarnagroup.cloud/apis/automower-connect-api
  - OpenAPI tab (the reference the user pointed at directly; same SPA
    problem, likely renders the spec client-side):
    https://developer.husqvarnagroup.cloud/apis/automower-connect-api?tab=openapi
  - "Status description and error codes" tab (would be the authoritative
    error-code source if it can ever be scraped — same SPA problem, worth
    retrying if browser tooling becomes available):
    https://developer.husqvarnagroup.cloud/apis/automower-connect-api?tab=status+description+and+error+codes
- **aioautomower** (Python, MIT, used by the Home Assistant Husqvarna
  integration) — source of the error code table and the stay-out-zones model,
  cross-checked against this account's real message history and found
  accurate for every code observed (9, 15, 17, 78, 92, 93, 94, 110, 123, 124,
  136, 157, 159, 160):
  - Repo: https://github.com/Thomas55555/aioautomower
  - Error codes: https://github.com/Thomas55555/aioautomower/blob/main/src/aioautomower/model/model_mower.py
  - Message model: https://github.com/Thomas55555/aioautomower/blob/main/src/aioautomower/model/model_message.py
  - Stay-out zones model: https://github.com/Thomas55555/aioautomower/blob/main/src/aioautomower/model/model_stay_out_zones.py
  - Positions model (confirms breadcrumb semantics, newest-first, max 50): https://github.com/Thomas55555/aioautomower/blob/main/src/aioautomower/model/model_positions.py
  - `utils.py` — reference implementation of the client-credentials token
    fetch and mower-list parsing; useful if this project grows into a fuller
    client (e.g. commands like start/stop/park):
    https://github.com/Thomas55555/aioautomower/blob/main/src/aioautomower/utils.py
- Gist with a partial (0-90) error code list, used before finding the fuller
  aioautomower source — superseded, kept here only for provenance:
  https://gist.github.com/nissicreative/277c80a23e83b5de923aadab050c186f

## This account's mowers (as of 2026-07-21)

| Name        | Model                          | Serial    |
|-------------|---------------------------------|-----------|
| AM405X      | Husqvarna Automower® 405X       | 210505449 |
| AM430X NERA | Husqvarna Automower® 430X NERA  | 232802869 |
| AM308V Nede | Automower® 308V                 | 260802646 |

(Authoritative live copy is `.data/mowers.json`, regenerated by `am list`.)
