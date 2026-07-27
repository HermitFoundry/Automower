# Session log

A running narrative of what happened in each work session on this project —
the reasoning, dead ends, and decisions behind the commits, not a
replacement for `git log`. `SKILL.md`/`README.md`/`qnap_infrastructure_setup.md`
hold the durable reference material this work produced; this file is the
story of how it got there. Newest entry on top.

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
