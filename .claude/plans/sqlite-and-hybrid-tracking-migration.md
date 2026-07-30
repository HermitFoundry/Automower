<!--
Copied into the repo 2026-07-30 from Claude Code's local plan-mode file
(originally C:\Users\TerjeSandstrom\.claude\plans\cozy-sauteeing-sky.md,
which only lives on this machine, not version-controlled) so it survives
across machines/sessions. Status: complete - the cutover described here
has already shipped to production (see SESSION_LOG.md's 2026-07-30 entry
and docs/database-schema.md for the resulting design).
-->

# Storage migration (SQLite) + hybrid event-driven tracking

## Context

Two previously-separate threads, now deliberately sequenced together
(decided 2026-07-30): the already-discussed SQLite storage migration
(`IMowerRepository`/`IMowerRegistry`, built 2026-07-29, JSONL-backed for
now - see that commit) and switching mower tracking from REST polling to a
WebSocket-event-driven hybrid (REST for what events can't cover, events for
everything else - see `SKILL.md`'s WebSocket research and this session's
real `eventtracking` experiments).

The reason to do them in this order, not the original "events now, SQLite
later" plan: on JSONL, every written line has to be a complete snapshot (the
whole point of Phase 1's design was keeping that shape unchanged so nothing
downstream had to change). Under real event volume (`position-event-v2`
firing roughly every 20-30s while mowing) that means either repeating every
unchanged field into a new line every time (real duplication - the exact
problem raised), or building debounce/heartbeat throttling to bound it
artificially. A relational schema doesn't have this problem at all: a row
can leave unrelated columns `NULL`, so a position-only event is a small,
genuinely non-duplicative insert - no throttling logic needed anywhere. So:
design the SQLite schema with the event use case in mind first, then build
the event hybrid on top of it - less total work than building JSONL
throttling logic now and discarding it once SQLite arrives anyway.

## Development setup (before any code changes)

Decided with the user (2026-07-30): build this on a feature branch, in a
completely separate QNAP deployment from production, so the existing
`main`-deployed `AutomowerWeb` and the `track` daemons for AM405X/AM430X
NERA are never disturbed while this is in progress. AM308V Nede is the one
mower used to build and validate against - convenient because it already
has the most real `eventtracking` runtime history from this session, and
the user can start it running whenever a real test needs live data.

- **Git**: a new feature branch off `main` (e.g. `feature/sqlite-event-
  tracking`) - all Part 1/Part 2 work happens there, merged to `main` only
  once validated.
- **Separate QNAP checkout**: a second directory on the host (e.g.
  `/repos/Automower-dev`, via `git worktree add` or a second clone) tracking
  the feature branch - `main`'s existing `/repos/Automower` checkout, which
  production `AutomowerWeb` and all 3 `track` daemons run from, is never
  touched by this work. The dev checkout gets its own `.config/config.json`
  (a straight copy of the existing one - same Husqvarna account/app key,
  just a second local checkout) and starts with its own empty `.data`.
- **New, separate web instance**: a new `AutomowerWeb` container (or at
  minimum a separate process on a different port) bind-mounted to the dev
  checkout, not the production one - so production's dashboard/details
  pages for all 3 mowers keep working exactly as today throughout.
- **AM308V runs two trackers in parallel on purpose, not by accident**:
  production's existing AM308V `track` daemon (on `main`, unchanged) keeps
  running the whole time - it's the "control" the plan's own verification
  steps already need for the JSONL-vs-SQLite and REST-vs-hybrid-event
  comparisons. The dev checkout runs the new code (first the SQLite-backed
  repository, later the hybrid event tracker) against the *same* real
  AM308V, writing into its own separate `.data`/SQLite db. Both reading the
  same live mower concurrently is fine (REST `GET`s aren't exclusive; the
  WebSocket account limit is 10 simultaneous connections, comfortable
  headroom for one extra).
- **AM405X and AM430X NERA**: untouched throughout - keep running on
  `main`, production, exactly as today. Nothing in Part 1 or Part 2 needs
  them until real rollout (after this is merged and validated).
- **Cutover**: once Part 1 and Part 2 are both validated against AM308V on
  the dev checkout, merge the feature branch to `main`, redeploy production
  normally (the existing `git pull` + `stopweb.sh`/`startweb.sh` and
  `stopall.sh`/`startall.sh` pattern used all session), then retire the dev
  checkout/container.

## Part 1: SQLite migration (do first)

### Already-decided (unchanged from the earlier design conversation)
- **Per-mower SQLite DB** (identical schema) + **one common DB** for the
  mower registry - no cross-mower queries are ever needed, and each mower
  already has its own independent writer process (today's 3 `track`
  daemons), so per-mower files avoid any write-lock contention between them
  even under SQLite's single-writer-at-a-time model.
- **Dapper + `Microsoft.Data.Sqlite`**, not EF Core - direct SQL, no
  migrations-framework ceremony, fits this codebase's existing preference
  for transparent code over ORM abstraction (see e.g. the raw
  `JsonDocument` parsing already used throughout `AutomowerConsole.Core`).
- **`IMowerRepository`/`IMowerRegistry`** (`AutomowerConsole.Core/
  MowerRepository.cs`, `MowerRegistry.cs`) is the seam - `SqliteMower
  Repository`/`SqliteMowerRegistry` implement the same interfaces
  `JsonlMowerRepository`/`JsonlMowerRegistry` do today, so callers
  (`TrackingService`, `CoverageService`, `ScheduleService`, `MowerService`,
  the web app) mostly don't change.

### New schema: raw source of truth + a derived, sparse query table

Two tables, not one - caught during review: a schema that only stores
pre-extracted columns throws away exactly the thing that made today's
JSONL logs valuable for after-the-fact debugging (e.g. the coverage-map
stale-buffer bug and the daily-statistics work this session were only
diagnosable/fixable because the *original* raw REST JSON was still sitting
in `track-<mower>.jsonl`, not just whatever fields had already been
extracted from it at the time). So:

**`RawEvents`** (per-mower DB) - the permanent, unprocessed source of
truth. One row per REST poll response or WebSocket event received,
storing the payload exactly as it arrived - nothing derived, nothing
dropped. Directly unifies what are two separate files today
(`track-<mower>.jsonl`'s full REST snapshots, `events-<mower>.jsonl`'s raw
WebSocket messages) into one common raw log, covering both poll and event
data the same way, as asked.
```sql
CREATE TABLE RawEvents (
    Id INTEGER PRIMARY KEY AUTOINCREMENT,
    Timestamp TEXT NOT NULL,
    Source TEXT NOT NULL,    -- 'rest' | 'event:mower-event-v2' | 'event:battery-event-v2' | ... | 'event:ready' (handshake)
    RawJson TEXT NOT NULL    -- exactly what was received, unprocessed
);
CREATE INDEX idx_rawevents_timestamp ON RawEvents(Timestamp);
```
Naturally non-duplicative already, with no special-casing needed: a REST
response is whatever size a full snapshot actually is, an event payload is
whatever size that event's own delta actually is - nothing is padded out
or repeated to match a common shape.

**`Observations`** (per-mower DB) - a *derived*, sparse, query-friendly
table built from `RawEvents`, not an independent source of truth. Only the
columns a given raw record actually carries get populated; everything else
stays `NULL`. This is what directly solves "duplicating polled data for
every event": a battery-only event derives one small row with just
`BatteryPercent` filled in, not a repeat of activity/position/everything
else. Because it's fully derivable from `RawEvents`, it can always be
**rebuilt from scratch** if the extraction logic that produces it turns out
to have a bug or is missing a field someone later wants - the raw data
underneath never has to change for that fix to be possible.
```sql
CREATE TABLE Observations (
    Id INTEGER PRIMARY KEY AUTOINCREMENT,
    RawEventId INTEGER NOT NULL REFERENCES RawEvents(Id),
    Timestamp TEXT NOT NULL,
    Source TEXT NOT NULL,
    Activity TEXT NULL,
    WorkAreaId INTEGER NULL,
    BatteryPercent INTEGER NULL,
    Latitude REAL NULL,
    Longitude REAL NULL,
    PlannerNextStartTimestamp INTEGER NULL
);
CREATE INDEX idx_observations_timestamp ON Observations(Timestamp);
```
`statistics`/`progress`/`workAreas[]`/`stayOutZones`/`capabilities`/schedule
deliberately don't live in `Observations` - see the scope clarification
below. They're still fully recoverable from `RawEvents` regardless (a REST
poll's raw JSON has everything), `Observations` just doesn't bother
projecting them out since nothing queries them that way today.

**`DailyStatistics`** - same shape/purpose as today's
`statistics-<mower>.jsonl`: `(Date TEXT PRIMARY KEY, <the 10 StatisticsInfo
columns>)`. The existing backfill/day-rollover/season logic
(`TrackingService.GroupIntoSeasons`, `AggregateDailyActivity`, etc.) is
already pure and operates on `DailyStatisticsSnapshot` records regardless
of where they came from - unchanged.

**`Schedule`** - same as today's `schedule-<mower>.json`: cached
`CalendarTask[]` + fetch time.

**Common DB**: `Mowers` table replacing `mowers.json` (`Id`, `Name`,
`Model`, `SerialNumber`).

### Scope clarification that shrinks this plan (worth stating plainly)

`progress`, `workAreas[]` definitions, `stayOutZones`, and `capabilities`
are **already** fetched live by the web app on every page load
(`MowerDetailService.GetMowerDetailAsync` in `Dashboard.razor`/
`MowerDetails.razor`'s `OnInitializedAsync`) - they never flow through
`track-<mower>.jsonl`/`IMowerRepository` today and don't need to going
forward. Same for the schedule/next-calendar-start actually *displayed* on
`MowerDetails.razor` - also a live fetch from `_detail.Attributes.Calendar`,
not the cached schedule file (that cache is only consumed by
`TrackingService.RunAsync`'s own interval-decision logic and the CLI's
`sessions --calendar` flag). So the only REST-only field that genuinely
needs to keep flowing through the historical pipeline is `statistics` (for
`DailyStatistics`/Seasons) - everything else REST-only was already a
non-issue, no design needed.

### `IMowerRepository` interface change (the one real seam change)

`AppendPollAsync(mowerId, rawJson, timestamp, ct)` - today takes a whole raw
REST blob and writes exactly one line - becomes a raw-write-plus-derive:
```csharp
// Writes the raw payload to RawEvents unconditionally, derives an
// Observation from it using the current extraction logic, writes that too.
// The common-path write for both a REST poll and (in Part 2) a WebSocket
// event - only what's passed in differs.
Task RecordAsync(DateTimeOffset timestamp, string source, string rawJson, CancellationToken ct = default);

// Re-derives every row in Observations from RawEvents using whatever the
// current extraction logic is - the recovery path when that logic turns
// out to have had a bug, or when a newly-added field needs backfilling
// into history that predates the code change. Never touches RawEvents.
Task RebuildObservationsAsync(CancellationToken ct = default);
```
A REST poll's `rawJson` is a full snapshot, so its derived `Observation`
populates every column; a WebSocket event's `rawJson` is just that event's
own delta, so its derived `Observation` only populates the columns that
event type actually carries - the extraction step (mapping `source` + raw
JSON shape to which columns get set) lives inside `RecordAsync`'s
implementation, not in the caller.

`GetHistory()` keeps returning the exact same `MowerHistory`/`PollRecord`
shape callers already consume - `SummarizeSessions`, `CoverageService`,
etc. need **zero** changes. `SqliteMowerRepository.GetHistory()` runs a
plain `SELECT * FROM Observations ORDER BY Timestamp` via Dapper, then
reconstructs fully-populated `PollRecord`s with a straightforward C#
carry-forward loop (each column keeps its last known non-null value) -
same technique today's JSONL `GetHistory()` already effectively uses for
`workAreaNames`/`latestCalendarTasks` (accumulating across a sequential
scan), just extended to every column, kept in testable C# rather than
leaning on SQLite's limited "ignore nulls" window-function support.

### Migrating existing data

A one-time migration reads each mower's already-collected
`track-<mower>.jsonl` **and** `events-<mower>.jsonl` line-by-line and
writes each one into `RawEvents` as-is (`Source = "rest"` for track lines,
`Source = "event:<type>"`/`"event:ready"` for event lines, parsed straight
from what's already in each line) - the two separate raw logs become one
common raw table, as asked. `RecordAsync`'s normal derive-and-write path
then produces `Observations` from that (for the track lines this is
one-for-one with what `JsonlMowerRepository.GetHistory()` already parses
out of them today; historical event lines derive whatever `Observations`
columns their event type carries, same as a live one would). `statistics-
<mower>.jsonl` migrates into `DailyStatistics`, `schedule-<mower>.json`
into `Schedule`, `mowers.json` into the common DB's `Mowers` table. Verify
by diffing `sessions`/`daily`/`coverage`/`seasons` CLI output between the
JSONL-backed and SQLite-backed repository for the same mower/history before
switching over for real.

**Verified 2026-07-30 against real AM308V data** (1202 REST polls, 1012
WebSocket events, 9 daily-statistics snapshots, 1 schedule, all 3 mowers'
registry entries): `sessions`/`daily`/`seasons` matched exactly once
filtered to REST-only Observations (the one difference was the still-
"ongoing" session's duration, which differs by exactly the wall-clock time
between the two capture runs - not a data discrepancy). One real bug found
and fixed along the way: `GetHistory()`'s `Query<ObservationRow>` crashed
at runtime - Dapper couldn't materialize a `private record` nested inside
`SqliteMowerRepository`, even with a constructor that matched the query's
columns exactly. Fixed by mapping from dynamic rows by hand, same approach
already used for `StatisticsInfo`.

Also surfaced something not a bug, but worth noting: migrating **both**
`track-<mower>.jsonl` and `events-<mower>.jsonl` into the same `RawEvents`
table (as designed) means the SQLite-backed history is now *more complete*
than the JSONL-backed one ever was - `JsonlMowerRepository.GetHistory()`
never reads `events-<mower>.jsonl` at all, so real WebSocket data from this
session's `eventtracking` experiments (denser than REST polling, e.g. two
distinct leave-the-dock attempts on 2026-07-30 that REST-only history
collapses into one) only becomes visible in `sessions`/`daily` once fully
migrated. Not a discrepancy to fix - the expected, intended benefit of
unifying the two raw sources - but explains why an unfiltered before/after
diff won't be identical, and confirms the migration doesn't need to be
byte-for-byte equivalent to be correct.

### Rollout (on the feature branch / dev checkout - see "Development setup")
1. Build `SqliteMowerRepository`/`SqliteMowerRegistry` behind the existing
   interfaces (one `.db` file per mower, **WAL mode enabled** so the dev
   web instance can read concurrently while a daemon writes, without
   blocking).
2. Build the migration tool; run it against AM308V's real data (copied from
   production's `.data` into the dev checkout); diff output against the
   JSONL-backed repository for the same mower to confirm the carry-forward
   reconstruction is correct.
3. Swap DI registration (`JsonlMowerRepository`/`JsonlMowerRegistry` ->
   `Sqlite*`) in the dev checkout once confident - AM308V's production
   JSONL files stay untouched and keep being written by production's own
   unchanged `track` daemon throughout.
4. Once Part 2 is also validated against AM308V (below), merge to `main`
   and roll out to all 3 mowers for real as part of the cutover.

## Part 2: Hybrid event-driven tracking (built on the new schema)

With `RawEvents` + derived `Observations` in place, this is substantially
simpler than it would have been on JSONL - **no debounce/heartbeat
throttling logic needed at all**. Every WebSocket event becomes one call to
`RecordAsync(timestamp, "event:<type>", rawEventJson)` - the raw payload
(small, since events are already deltas) lands in `RawEvents` unconditionally,
and its derived `Observation` only populates whatever columns that event
type actually carries. At real volume (roughly one position event per
20-30s during active mowing, per this session's `eventtracking` captures -
a few hundred rows per mow session, tens of thousands across a season) this
is trivial for SQLite, and never duplicative.

### Design
- Reuse `EventTrackingService.ListenOnceAsync`'s connect/reconnect/2h-
  proactive-reconnect logic (`AutomowerConsole.Core/EventTrackingService.cs`)
  - extract the shared low-level plumbing rather than reimplementing it, so
  `EventTrackingService` and the new loop share one reconnect
  implementation.
- For every event matching a mower: call `RecordAsync` with the raw event
  JSON and `Source = "event:<type>"`. No merge-cache, no debounce, no
  "meaningful change" check needed - every event is recorded as-is, cheaply,
  every time.
- A much-reduced-frequency REST poll (`Config.RestRefreshIntervalSeconds`,
  proposed 900s/15min) still runs, purely to keep `DailyStatistics` fresh
  (the one field events never carry) - calls `RecordAsync` with
  `Source = "rest"`, same as any REST-sourced row, deriving a fully-
  populated `Observation` since a REST response is always a complete
  snapshot.
- Schedule caching (`ScheduleService.SaveScheduleForMower`) still comes from
  that same REST-refresh poll's embedded `calendar`, not `calendar-event-v2`
  - confirmed too unreliable (fired only twice in a long real session, weak
  correlation with actual calendar changes).
- `EventTrackingService`'s standalone command becomes redundant once the
  hybrid loop exists - both write every event into the same `RawEvents`
  table now, so there's no longer a distinct "raw event archive" only the
  standalone command produces. Likely retired at that point rather than
  kept as a second path into the same data; final call at implementation
  time once the hybrid loop is proven out.

### Open technical question - resolved 2026-07-30
Confirmed against real captured `events-AM308V-Nede.jsonl` data:
`position-event-v2`'s payload is `attributes.position.{latitude,longitude}`
- a **single point**, not an array like REST's `positions[]`. Better than
REST for coverage purposes, not just equivalent - no stale-buffer risk is
even possible, since there's no buffer to begin with, each event is one
fresh, isolated fix. Also confirmed real shapes for the other 3 fields
`Observations` tracks, all nested the same way REST nests them (just
partial): `mower-event-v2` -> `attributes.mower.{activity,workAreaId}`,
`battery-event-v2` -> `attributes.battery.batteryPercent`,
`planner-event-v2` -> `attributes.planner.nextStartTimestamp`.

### Rollout (still on the feature branch / dev checkout)
1. Verify `position-event-v2`'s shape. **Done.**
2. Extract shared WebSocket plumbing from `EventTrackingService`. **Done.**
3. Build the event-consuming loop (`RecordAsync` per event) + the
   REST-refresh timer. **Done** - `HybridTrackingService`, wired up as
   `hybrid-track [mower]`.
4. **Validate against AM308V** - `hybrid-track "AM308V Nede"` started
   2026-07-30 ~13:53 in tmux session `hybrid-am308v` on the dev checkout
   (SQLite-backed), running concurrently with production's own unmodified
   `automower-AM308V` `track` daemon on `main` - both confirmed alive
   (REST refresh + WebSocket connected). User sent AM308V out to mow
   `hovedomrade` shortly after - a real, live mow session is exactly the
   validation case this step needs (dense WebSocket activity to compare
   against REST-only precision). Once there's enough real data, compare
   `sessions`/`daily`/`coverage`/`seasons` output between the two for the
   same real day(s); confirm the precision improvement shows up (catches
   transitions polling misses) with no regression.
5. **Cutover**: merge the feature branch to `main`, redeploy production
   (`git pull` + the existing `stopweb.sh`/`startweb.sh`,
   `stopall.sh`/`startall.sh` pattern), retire the dev checkout/container.
   Roll out the hybrid tracker to AM405X/AM430X NERA, retiring their
   per-mower pure-REST `track` invocation (the hybrid loop's REST-refresh
   timer replaces it).

**Step 4 completed and validated 2026-07-30**, against one full real mow
session on `hovedomrade` (14:20-15:43, including a genuine ~2min GPS/signal
hiccup, `Inactive reason: SEARCHING_FOR_SATELLITES`, mid-mow) - compared
`sessions` output between production's REST-only tracker and the SQLite-
backed hybrid one for the exact same session. Same overall story on both
sides, no data lost or contradicted anywhere; the hybrid side is
consistently denser at real transition boundaries (e.g. resolves the
end-of-mow/going-home moment into distinct sub-transitions where the
REST-only view smooths it into one block) - the intended precision
improvement, confirmed live rather than just in theory. Also incidentally
confirmed, while investigating what looked like odd dashboard values during
this same session: `workAreas[].progress` is a genuinely live, non-
monotonic value in Husqvarna's own API (observed 0% -> 4% -> 2% -> 4%
within a few minutes, verified against the raw JSON payload each time, not
a caching or display bug on our side) - worth remembering if progress is
ever used for anything beyond a rough display.

**Cutover completed 2026-07-30.** Merged to `main` (fast-forward), all 3
mowers' real production data migrated (AM308V: 1303 REST polls/1245
events, AM405X: 1760 REST polls/0 events, AM430X NERA: 2940 REST
polls/1025 events - all cleanly, zero malformed lines), production
`AutomowerWeb` redeployed SQLite-backed, all 3 `track` daemons replaced
with `hybrid-track` (confirmed REST refresh + WebSocket connected for all
3 - AM308V hit one transient 403 on its first connection attempt, self-
healed via the existing 5s-retry reconnect logic, a real live confirmation
that path works). Standalone `eventtracking-AM308V`/`eventtracking-AM430X`
experiments stopped (superseded). Dev checkout/container fully retired,
verified via both the QNAP host filesystem and from inside `debian-dev1` -
no data loss, production's `Automower` directory untouched throughout.

Plan complete.

## Files likely touched

- **Part 1**: new `SqliteMowerRepository.cs`/`SqliteMowerRegistry.cs`
  (`AutomowerConsole.Core`), implementing `RecordAsync`/
  `RebuildObservationsAsync`/`GetHistory()` against the `RawEvents`+
  `Observations` schema; a migration tool/CLI command; `IMowerRepository`'s
  `AppendPollAsync` -> `RecordAsync` signature change and its one current
  caller (`TrackingService.RunAsync`); `AutomowerConsole.csproj`/
  `AutomowerConsole.Core.csproj` add `Microsoft.Data.Sqlite` + `Dapper`
  package references.
- **Part 2**: new shared WebSocket plumbing (extracted from
  `EventTrackingService.cs`); new event-consuming loop (`TrackingService.cs`
  or a sibling class); `Models.cs` adds `Config.RestRefreshIntervalSeconds`.
- `SKILL.md` - document both once shipped, same as every other API-behavior
  finding this session.

## Verification

- **Part 1**: the migration diff (JSONL-backed vs. SQLite-backed repository
  output for the same real mower history) is the primary check; existing
  unit tests (`GroupIntoSeasons`, `AggregateDailyActivity`, etc.) stay green
  unchanged since they operate on the repository's output shape, not its
  backend.
- **Part 2**: the parallel-run comparison on AM308V (rollout step 4), same
  validation pattern already used for the coverage-map and daily-statistics
  features this session.
