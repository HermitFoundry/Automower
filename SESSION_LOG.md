# Session log

A running narrative of what happened in each work session on this project —
the reasoning, dead ends, and decisions behind the commits, not a
replacement for `git log`. `SKILL.md`/`README.md`/`docs/` hold the durable
reference material this work produced; this file is the story of how it
got there. Newest entry on top.

## 2026-07-30

**SQLite migration + WebSocket hybrid tracking, designed, built, and cut
over to production in one session.** Started as a routine "should we move
off JSONL" design conversation (per-mower SQLite db + a common db,
Dapper not EF Core - all agreed quickly, low-risk) and grew into the
biggest architectural change this project has made: mower tracking is now
event-driven, not poll-driven, and storage is SQLite everywhere. See
`docs/database-schema.md` for the resulting schema; this entry is the
story of how it got decided and what broke along the way.

**The real design decision: sequence SQLite before events, not after.**
The original plan was "hybrid events now, SQLite storage later" - already
under discussion (see 2026-07-29's repository-pattern entry). Revisited
when the user asked the sharp question that mattered: on JSONL, every
written line has to be a complete snapshot, so real event volume
(`position-event-v2` fires every 20-30s while mowing) means either
duplicating every unchanged field into a new line every time, or building
debounce/heartbeat throttling to bound it. A relational schema doesn't
have that problem - a row can leave unrelated columns `NULL`. So: design
the SQLite schema with the event use case in mind *first*, build the
event hybrid on top of it *second* - less total work than building JSONL
throttling logic and discarding it once SQLite arrived anyway. This
reordering is why `HybridTrackingService` ended up with zero debounce
logic at all - every WebSocket event is just a cheap, genuinely sparse
insert, exactly what the schema was shaped for.

**Schema: `RawEvents` (raw, unprocessed) + `Observations` (derived,
sparse), not one table.** Caught during plan review, before any code was
written: a schema that only stores pre-extracted columns throws away
exactly what made JSONL logs valuable for after-the-fact debugging (the
2026-07-29 coverage-map bug and the daily-statistics work were only
diagnosable because the *original* raw JSON was still on disk). So
`RawEvents` is the permanent, unprocessed source of truth (one row per
REST poll or WebSocket event, exactly as received - unifies what were two
separate JSONL files); `Observations` is a derived, sparse table built
from it, rebuildable from scratch at any time if the extraction logic
ever needs fixing, without touching the raw data.

**Dev environment: a real second QNAP deployment, not just a branch.**
Built on `feature/sqlite-event-tracking`, in a second checkout
(`/repos/Automower-dev`) and a second `AutomowerWeb-dev` container (port
5153), so production (`AutomowerWeb` on 5152, all 3 `track` daemons)
stayed completely untouched through the whole build. AM308V Nede was the
one mower used to validate against - it kept running its *production*
`track` daemon the whole time as the "control," while the dev checkout's
new code ran against the same real mower concurrently. Confirmed safe:
REST `GET`s aren't exclusive, WebSocket's 10-connection account limit had
plenty of headroom for one extra.

**Real bugs, only found by actually running it, not by building it:**
- `GetHistory()`'s `Query<ObservationRow>` crashed at runtime - Dapper
  couldn't materialize a `private record` nested inside
  `SqliteMowerRepository`, even though its constructor matched the
  query's columns exactly. Fixed by mapping from dynamic rows by hand
  (same approach already used for `StatisticsInfo`, which was written
  defensively for exactly this kind of doubt up front).
- The first JSONL-vs-SQLite diff looked wrong (lots of extra rows on the
  SQLite side) - turned out to be a flawed comparison, not a bug: the
  migration pulls in `events-<mower>.jsonl` as well as
  `track-<mower>.jsonl`, and `JsonlMowerRepository.GetHistory()` never
  read the events file at all. Filtering the SQLite side to REST-only
  Observations before diffing showed an exact match (the one remaining
  difference was the still-"ongoing" session's duration, which differs by
  exactly the wall-clock time between the two capture runs).

**Live validation, not just synthetic:** the user sent AM308V out to
mow `hovedomrade` for real while both trackers were running. The hybrid
tracker resolved the whole leave-the-dock sequence (workAreaId set ->
NOT_APPLICABLE -> CHARGING -> LEAVING -> MOWING) within about a minute at
second-level precision - a transition REST polling would have blurred or
missed. Also caught a genuine ~2-minute `NOT_APPLICABLE`/
`SEARCHING_FOR_SATELLITES` episode mid-mow (confirmed against the raw
payload: no diagnostic detail beyond the bare label - Husqvarna's API
really does only tell you *that* it's searching, never *why*). Confirmed
`workAreas[].progress` is a genuinely live, non-monotonic value in
Husqvarna's own API (0% -> 4% -> 2% -> 4% within a few minutes, verified
against the raw JSON each time) - not a caching or display bug on our
side, just a real characteristic worth remembering if progress is ever
trusted for anything beyond a rough display. Coverage density confirmed
concretely too: 627 REST-only points vs. 1,030 once WebSocket
`position-event-v2` data was included for the same mower - about 65%
denser, since events fire every 20-30s vs. REST's 60s, each a single
fresh GPS fix with no stale-buffer risk (confirmed `position-event-v2` is
a single point, not an array like REST's `positions[]`).

**Cutover:** merged to `main`, migrated all 3 mowers' real production
history (AM308V 1303 polls/1245 events, AM405X 1760 polls/0 events,
AM430X NERA 2940 polls/1025 events - zero malformed lines anywhere),
redeployed `AutomowerWeb`, replaced all 3 `track` daemons with
`hybrid-track` (`startall.sh` updated), retired the standalone
`eventtracking` experiments (superseded) and the dev checkout/container.
One scare during cleanup: a verification `ls /repos/` after `rm -rf
/repos/Automower-dev` failed with "No such file or directory" - looked
like `/repos` itself might be gone, but was just a command-construction
mistake (that `ls` ran on the QNAP host, where the path is `/share/Repos`,
not `/repos` - that mapping only exists inside containers). Confirmed via
the host filesystem directly: production untouched, only the intended
directory removed.

**Post-cutover accuracy pass, prompted by the user asking a very direct
verification question** ("is all data now in SQLite, and is the schema
documented?"). Answering it properly surfaced two real, user-facing bugs
that a "does it still build" check would never have caught: `sessions`/
`daily`/`monthly`/`track`/`eventtracking`'s own console output was still
printing the old JSONL file paths in their "from .../logging to ..."
messages, even though the data genuinely lives in SQLite now - fixed to
print `Storage.GetMowerDbPath` throughout. `README.md`/`docs/database-
schema.md`/`docs/tracking.md` were also still framing SQLite as a
"feature branch alternative" instead of the current default - fixed.

**Repo durability pass**, prompted by the user asking what would be lost
closing and reopening this Claude Code session. Three gaps closed: the
SQLite/hybrid-tracking plan only existed in Claude Code's local, per-machine
plan cache (`C:\Users\...\.claude\plans\cozy-sauteeing-sky.md`) - copied
into `.claude/plans/sqlite-and-hybrid-tracking-migration.md` so it survives
a machine change, not just a session restart. QNAP operational knowledge
(SSH two-hop access, `docker` not on `PATH` for non-interactive shells, the
`/repos` vs `/share/Repos` host/container path trap, the nested-quoting
workaround of piping a scratchpad script through stdin) existed only as
narrative in `docs/qnap_infrastructure_setup.md` - added a dedicated
`.claude/skills/qnap-ops/SKILL.md` alongside it, the operational
counterpart to that doc, triggered automatically by a Claude Code session
rather than needing to be found by hand. And a repo-root `CLAUDE.md` now
gives a fresh session a way to self-confirm it picked up this repo's
context after a restart (answers "Who are you?" with a fixed identity
string, per the user's request).

**README restructuring, finally done.** The outline from the "Next up"
note below was agreed and executed: `README.md` now leads with "What this
is" and a full "Web dashboard" section (the primary interface now, not an
afterthought), and CLI/installation/design/configuration each get a short
intro plus a link out to a new `docs/` page (`cli-usage.md`,
`installation.md`, `design.md`, `configuration.md`) instead of being
inlined. No longer reads as "a small C# console client."

**Coverage map: dot size tuned, and a real cross-area color-mixing bug
found and fixed.** The user first asked for the coverage dots to be a
quarter their size (`r="0.3"` -> `r="0.075"`), then - after looking at
AM430X NERA's map - reported them too small and asked to double them
(`r="0.15"`), and separately flagged that "oversiden" now showed dots in
three colors (green/violet/brown) mixed together, recalling that "we had a
protection for that earlier."

That protection was real and specific: `CoverageService`'s own comment
already documented it - under REST-only polling, a poll's `WorkAreaId` and
`Latitude`/`Longitude` always came from the same atomic snapshot, so
pairing them for area-colored plotting was always safe. Hybrid-track
quietly broke that guarantee without anyone noticing at the time: a
`position-event-v2` row only carries a fresh GPS fix, so its `WorkAreaId`
in the reconstructed `PollRecord` is whatever a `mower-event-v2` last set -
potentially several minutes stale, long enough to span a real work-area
change. The result: GPS fixes genuinely inside "oversiden" got plotted in
whatever color a stale, carried-forward `WorkAreaId` said, bleeding other
areas' colors into it.

Fixed at the source rather than papered over in the UI: `PollRecord` gained
`WorkAreaIdObservedAt` (the timestamp of whichever observation actually
last set `WorkAreaId` - identical to the poll's own timestamp for a REST
row, since those are always atomic; tracked as a separate running value in
`SqliteMowerRepository.GetHistory()`'s carry-forward scan for hybrid-
tracked rows). `CoverageService` now skips a point if its `WorkAreaId` is
more than 5 minutes stale relative to its own timestamp - restores the old
atomicity guarantee approximately, trading a little point density near real
transitions for not mislabeling points into the wrong area's color. User
confirmed correct after redeploy.

## 2026-07-29

**The eventtracking experiment kept running overnight and validated the
reconnect logic for real.** Two sessions (AM308V, AM430X NERA) survived a
full 8+ hour overnight span across 7 proactive reconnect cycles with zero
crashes. Surfaced one more real finding along the way: AM308V's connection
took 3 early server-initiated closes (well under the documented 2h limit,
irregular intervals) before settling into a clean exact-2-hour cadence,
while AM430X's connection never had a single early close - the official
"max 2 hours" language apparently doesn't mean every connection routinely
lasts that long.

**A GPS boundary-reconstruction detour, which turned into a real feature.**
The user asked whether a work area's outer boundary could be reconstructed
from the accumulated position data. A first pass (a one-off Python script,
convex hull, grouped by `workAreaId` alone) produced visibly wrong
boundaries - the user caught it immediately ("draws different non-related
areas"). Investigation found the real cause: a poll's full `positions[]`
breadcrumb array (up to 50 entries) can still hold several minutes of the
mower's *previous* stay in a different work area (e.g. parked near a
charger) for many polls after it's actually moved on - confirmed
unambiguously by 3 real episodes where the contamination count decayed by
exactly 2 per poll, the signature of a sliding buffer aging out stale
history. A simple distance-from-current-position cutoff didn't work either
- normal within-poll spread is commonly 20-40m on its own. The fix that
actually worked: use only `positions[0]` (the newest breadcrumb, always
synchronized with that same poll's own `workAreaId`) per poll, discarding
the rest of the array entirely - confirmed empirically to drop the outlier
count to zero while barely denting real coverage density.

This became a real, permanent feature: `CoverageService` (reads a track
log, extracts per-work-area GPS coverage) plus a new "Coverage
(experimental)" section at the bottom of the mower details page - dots
only, no computed boundary polygon yet, deliberately live-computed in the
web app rather than a separate background process, per the user's own call
to validate the shape quality first. Confirmed clean by the user across all
three mowers after the fix, including a single-work-area mower (AM405X)
where the same fix resolved a differently-caused version of the same
underlying problem (stale history from a charger stay, just without a
second work area ID to distinguish it).

One loose end chased down out of general due diligence, not because it
turned out to be a real problem: the user's original idea for catching bad
GPS fixes was a speed check between consecutive points. Once the coverage
fix gave genuinely clean one-point-per-poll data with reliable ~60s
inter-poll timing, that became straightforward to actually compute - real
observed max speeds across all three mowers topped out under 0.5 m/s, zero
flagged flukes with a generous 1.5 m/s threshold. A recurring, tight,
5-day-consistent cluster on AM405X near a house wall turned out not to be a
fluke at all - real GPS multipath bias from working close to a building,
left alone rather than "fixed."

## 2026-07-28

**Dashboard clock chart, iterated to actually work.** Built a per-mower
"today at a glance" SVG pie chart for the dashboard (Mowing/Charging
wedges around a 06:00-18:00 clock face) plus new Mowed/Charging total
lines - then fixed it through several rounds of real user feedback: the
first version anchored 06:00 at the top instead of true clock position (6
belongs at the bottom, like a real analog clock), grey wedges and a white
"no data" background looked like two different things when they should
have been one, the app-wide green/blue accent colors didn't read against
the chart's own background in dark mode (dedicated `--chart-mowing`/
`--chart-charging` colors fixed it, then the grey background itself needed
lightening too), and a work-area name breaking the "Mowed" line onto a
second line threw off vertical alignment across cards (fixed alongside an
unrelated but similar issue: Nominatim's `Municipality` field baking the
Swedish/Norwegian word for "municipality" straight into the place name -
"Piteå kommun" instead of "Piteå").

**A real, self-healing bug fix**: the dashboard occasionally failed to
load with "Simultaneous logins detected" - traced to `AutomowerConnect`'s
*initial* authentication call having zero retry at all (unlike the
retry-after-a-failed-API-call path, which already had one). `AutomowerWeb`
is one of several independent clients sharing the same app key/secret
alongside the 3 `track` daemons, and Husqvarna's auth service rejects a
token request if another one for the same client id lands too close in
time - a real, transient, self-clearing collision (confirmed by the user:
reloading moments later always worked). Now retries up to 3 times with a
short delay, specifically for that error, so a reload is no longer needed.

**Work area progress and cutting pattern**, prompted by the user noticing
the Husqvarna app showing "0% done in this area" and asking whether that
was available anywhere in the API. It was - `progress`, `type`
(`RANDOM`/`SYSTEMATIC`), `orientation`, and `lastTimeCompleted` were all
real fields this project had never parsed. Confirmed live that `progress`
only exists on a `SYSTEMATIC` (EPOS-guided) work area at all - a `RANDOM`
one's raw JSON omits the whole group of fields entirely, not just reports
them as null. Added to the CLI, the mower details page's work-area table,
and (per a follow-up request) the dashboard's top block too, alongside a
newly-added Override row.

**The WebSocket event-push API, actually tried this time.** Built an
experimental `eventtracking` command and ran it standalone (its own tmux
session, deliberately not part of the managed `track` fleet) against the
AM308V for several hours across two real mowing sessions, then started a
second one against the AM430X NERA for its more complex route the next
day. This produced a genuine, evidence-based upgrade over the earlier
"confirmed usable" research from two days prior:

- Directly compared against `track`'s concurrent polling for the same
  window and found a real, measured gap - 14 straight one-minute polls
  that missed an entire cluster of state transitions (stop, pause, a
  work-area reassignment, resume) that happened and fully resolved
  *between* two polls, plus a second hidden departure attempt that
  polling's own flat record gave no sign of.
- Discovered the reverse gap too: no WebSocket event type exists at all
  for lifetime statistics, `stayOutZones`, work-area *definitions*, or
  work-area `progress` - confirming aioautomower's own hybrid poll+
  websocket design wasn't incidental, and a future switch could never go
  100% websocket-only.
- Caught a real, provable correction to the "events fire instantly on
  change" assumption: a genuine 96%→100% charge produced exactly one
  event with nothing in between, and two byte-identical `100%` battery
  events arrived 4 minutes apart with zero actual change - a pure
  edge-triggered model can't explain a duplicate. A `message-event-v2`
  re-announcing a week-old message twice, an hour apart, nailed the
  explanation down further.
- The user then got the *official* explanation pasted directly from the
  developer portal (a JS SPA `WebFetch` still can't render): the
  WebSocket has a real, documented 2-hour connection limit (confirmed,
  not just inferred from aioautomower's own defensive reconnect timer -
  which our own implementation had already matched), and - the piece that
  reconciled everything - the mower itself throttles down to a 15-minute
  check-in cadence after 10 minutes idle, to save battery/data. That one
  fact explained both the quiet stretches during parked/idle periods and
  the duplicate-event pattern as documented power-saving behavior, not a
  quirk. Also got the REST API's own quota (21,000/week, 120/minute per
  app key) and sanity-checked it against real usage - comfortably under a
  third of the weekly budget even with three always-on `track` daemons
  plus a day of ad hoc testing on top.
- Along the way, chased down why `track`'s own dashboard data went stale
  for the AM308V mid-session: not a display bug, but `config.json`'s
  `NightStartHour` having been set to `20` instead of the coded default of
  `22` - a fixed-interval poller that picks a 30-minute "night" sleep has
  no way to notice mid-sleep that manual mowing started. Fixed, and noted
  that this entire class of problem - guessing a poll interval by time of
  day at all - disappears once (if) `eventtracking` ever replaces polling
  for real.

Working pattern that showed up clearly this session: real findings kept
overturning earlier inferences (the "instant on change" assumption, the
"unexplained" idle gaps) once enough real data accumulated - a good
argument for treating even the aioautomower-sourced research as a
starting hypothesis to keep testing, not a settled fact, until official
documentation or enough real evidence confirms or corrects it.

## 2026-07-25 to 2026-07-27

**Public deployment finished, then hardened.** `AutomowerWeb` went from an
ad hoc tmux session sharing a container with the mower-tracking processes
to a real public deployment: its own container, a Caddy reverse proxy with
automatic Let's Encrypt TLS, and a `myqnapcloud.com` hostname behind an
Altibox port-forward. Getting there surfaced a string of real, unrelated
infra problems along the way - QNAP's dotnet SDK install via the
`dotnet-install.sh` tarball turned out to be broken on this specific
Container Station setup (a `tar` extraction bug plus a missing `libicu`
dependency), fixed by switching to an apt-based install once traced back to
how the original `debian-dev1` container had actually been provisioned
months earlier. Port 443 turned out to already be claimed by QTS's own
admin interface, so Caddy ended up on 8880/8443 instead, with the Altibox
forward doing the 80/443 → 8880/8443 translation.

**Two real security/correctness bugs found post-deployment, not before.**
First: the site was reachable over IPv6 directly to QTS's own admin login,
completely bypassing Caddy - Docker's port publish and the Altibox forward
are both IPv4-only, and the NAS had IPv6 enabled from before QTS started
defaulting it off. Fixed by disabling IPv6 on the NAS's own interface, no
downside since nothing here used it. Second: even after TLS was genuinely
valid, Edge (but not Chrome, not Edge Dev, not mobile) kept flagging the
site "not secure" - traced to Kestrel never being told the original request
was HTTPS (Caddy talks plain HTTP to it internally), which meant the
antiforgery cookie's `Secure` flag silently never got set. Fixed with
`UseForwardedHeaders` plus an explicit `SameAsRequest` cookie policy
(confirmed by direct testing that the forwarded-headers fix *alone* wasn't
enough - the antiforgery cookie doesn't follow `Request.IsHttps` the way a
plain cookie does). The lingering "Edge still shows insecure" turned out to
be stale per-profile browser state, not a real remaining issue, once
InPrivate/other browsers/a fresh Edge profile all showed it correctly as
secure.

**Session history growing unbounded, fixed with three tiers.** The mower
details page's session/daily history had no time limit at all - fine early
on, not once track logs span months. Session history is now windowed to a
rolling 7 days, daily rollup to a rolling month, and a new monthly rollup
(unwindowed, coarse enough not to matter) covers the long tail.

**A real charging-session data gap, found by inspecting a screenshot.** The
user noticed a `Charging` session showing `26%→26%` immediately followed by
a `Parked` session at `100%` - traced to raw track-log evidence showing a
genuine 1h43m polling gap. Root cause: `track` used to suppress repeat polls
while sitting at the charger (only logging arrival + the poll where battery
hit 100%), and if the mower's own `activity` label flipped from `CHARGING`
to `PARKED_IN_CS` on the exact same poll that crossed 100%, that poll became
the *start* of the next session instead of the *end* of this one - leaving
`BatteryEnd` stuck at the arrival value. Fixed two ways, both explicitly
decided by the user after weighing the options: `track` now logs every poll
unconditionally (removing the root cause going forward), and
`SummarizeSessions` backfills a charging session's `BatteryEnd` from the
next contiguous at-charger session's `BatteryStart` when they connect with
no time gap (fixing both new edge cases and all pre-existing history). A
"poll faster near 100%" optimization was considered and explicitly rejected
as not worth it once those two were in. Flagged as a consequence: log
volume grows faster now, so `track-*.jsonl` retention/truncation moved up
in priority (not yet done).

**Chasing what "Forced Mow" and a mystery `N/A` activity actually mean led
to a real, previously-unmodeled API field.** `planner.externalReason` (an
integer code identifying *what* external thing - a smart routine, a voice
assistant, IFTTT - is restricting the mower) was sitting unused in the raw
track logs the whole time, since `track` always logs the full raw API
response. Added to the model and surfaced in both the CLI and the web
dashboard. The one open question: this account's real data has only ever
shown code `6000` for any `EXTERNAL` restriction, which aioautomower labels
"Rain Guard" - but the user pointed out Husqvarna actually ships two
distinct weather-driven routines (Rain Guard vs. growth-based Weather
Timer), and it's not yet known whether `6000` is really only Rain Guard or
a code shared by both. Unresolved - needs a future occurrence where the
Husqvarna app itself names "Weather Timer" specifically, cross-referenced
against that moment's logged code.

**Official Husqvarna documentation replaced several reverse-engineered
guesses.** The user pasted the developer portal's own text (a JS SPA
`WebFetch` can't render, so this only happened because they copied it
directly) for `mode`/`activity`/`state` descriptions and the full error
code table. Corrected two real factual errors in the error code table
(codes 0 and 62) and, more interestingly, produced a correction to a
correction: the portal says `activity: NOT_APPLICABLE` means "manual start
required in mower," but the user confirmed no manual start happened around
a real occurrence - their own theory (temporary comms loss) plus an
independent comment from Husqvarna's own support (some status messages are
known to be overloaded/reused for more than one condition) is the
better-supported explanation. Recorded as genuinely unresolved rather than
picking one source and asserting it as fact.

**WebSocket event-push API investigated and confirmed usable, not
implemented.** The user asked to check whether Husqvarna's WebSocket API
(same SPA-doc problem) could eventually replace `track`'s polling.
Confirmed via aioautomower's actual client source (not just docs, real
working code that also powers the production Home Assistant integration):
same OAuth bearer token as REST, a ready-handshake then per-category delta
events, and a ~2-hour proactive reconnect cycle suggesting a server-side
connection lifetime. Deliberately not built - the user wanted it confirmed
and recorded for later, not implemented now. Full details in `SKILL.md`.

**Working pattern that emerged clearly this session**: research/investigate
first and report findings, wait for an explicit go-ahead before writing any
code - came up repeatedly (the WebSocket check, the smart-routine
investigation) and is now saved as a standing preference in memory.
