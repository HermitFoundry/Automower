# Session log

A running narrative of what happened in each work session on this project —
the reasoning, dead ends, and decisions behind the commits, not a
replacement for `git log`. `SKILL.md`/`README.md`/`qnap_infrastructure_setup.md`
hold the durable reference material this work produced; this file is the
story of how it got there. Newest entry on top.

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
