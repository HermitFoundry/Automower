---
name: automower-api
description: This skill should be used when working in the automower repo, or discussing the Husqvarna Automower Connect API, OAuth2 client-credentials auth against api.authentication.husqvarnagroup.dev, mower status/messages/work areas/stay-out zones, Automower error codes, or the aioautomower reference project. Triggers on "automower", "husqvarna", "mower api", "am.cmd".
version: 1.0.0
---

# Automower Connect API notes

Working knowledge for `C:\repos\automower`, a C# console app (`AutomowerConsole`,
net10.0) that talks to the Husqvarna Automower Connect API. Run via `am.cmd
<command> [args]` (forwards args with `%*`), or `dotnet run -- <command>`.

## Project layout

- `Program.cs` — CLI dispatch (top-level statements + local functions)
- `HusqvarnaClient.cs` — auth + HTTP calls
- `Models.cs` — JSON DTOs
- `Storage.cs` — reads/writes `config.json`, `mowers.json`, `state.json`, `schedule.json`
- `ErrorCodes.cs` — full error code → description table
- `config.json` — app key/secret + `track` interval settings; gitignored, holds a
  live secret. `config.example.json` (tracked) is the placeholder template.
- `mowers.json` / `state.json` / `schedule.json` — generated at runtime (mower cache,
  active selection, cached per-mower calendar); also gitignored

Commands: `config`, `config Key=Value ...`, `list`, `use <name|id|index>`,
`current`, `status [--all]`, `messages`, `errorcodes`, `workareas`,
`workarea <name|id>`, `stayoutzones`, `schedule [mower]`,
`track [seconds] [mower]`.

`config` with no args prints current `config.json` (AppKey/AppSecret masked
to first-4...last-4 chars); `config Key=Value ...` sets one or more fields via
reflection over the `Config` record in `Models.cs` (see `CommandConfig`) —
any new field added to `Config` is automatically settable this way, no CLI
wiring needed. It's also how `config.json` gets created in the first place
(`Storage.LoadConfigForEditing()`, unlike `Storage.LoadConfig()`, doesn't
require AppKey/AppSecret to already be set).

**Security history**: `config.json` with real credentials was accidentally
committed in this repo's first commit. Fixed by deleting the branch ref
(`git update-ref -d refs/heads/main` — safe since it was the only commit, no
remote existed yet) rather than just `git rm --cached`, so the secret never
existed in any reachable git history, not just future commits. If this ever
happens again *after* a remote exists, deleting the ref won't be enough —
history rewrite (or credential rotation) would be needed instead.

All mower-scoped commands (`status`, `messages`, `workareas`, `stayoutzones`,
`schedule`, the trailing arg on `workarea <name|id>`, and the mower arg on
`track`) accept an optional trailing `[mower]` override (name/id/list index)
to target a different mower for one call without changing the persisted
active selection in `state.json`. This goes through a shared
`ResolveMowerAsync` helper in `Program.cs`.

### `track` — adaptive-interval polling with logging

Polls `GET /mowers/{id}` and appends one JSON line per kept poll to
`track.jsonl` in the app's base directory: `{timestamp, mowerId, mowerName,
bytes, response}` where `response` is the full raw mower payload. Built to
answer "how much data would a day of polling actually be" empirically
instead of estimating — the log file's size on disk *is* the answer.

While the mower's `activity` is `CHARGING` or `PARKED_IN_CS` (see
`IsAtCharger`) **and** it's not inside a scheduled window, only the first
poll after arrival at the charger is logged — repeat polls while still
parked are skipped (console-only line), to avoid wasting log volume on an
unchanging state.

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

The schedule used for decision #1 comes from `schedule.json`, refreshed for
free from every `track` poll's own `attributes.calendar` (the mower payload
already includes it — no extra API call). `schedule.json` is a dict keyed by
mower id: `{mowerId: {MowerName, FetchedAt, Tasks}}`, where `Tasks` is the
same `CalendarTask[]` shape used by `workarea`'s per-area schedule, but with
`workAreaId` populated per task (confirmed present on the mower-level
`GET /mowers/{id}` calendar too, not just the per-work-area endpoint). Run
`schedule [mower]` standalone to force a refresh/inspect it without starting
`track` (e.g. right after changing the schedule in the app).

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
- `capabilities.stayOutZones: true` only means the mower *supports* the
  feature — the `stayOutZones` attribute itself is `null` when no zones are
  configured (confirmed on all 3 of this account's mowers). When present, the
  shape (per aioautomower's model, not yet confirmed against a live response)
  is `{ "dirty": bool, "zones": [ {"id": str, "name": str, "enabled": bool} ] }`.
- The developer portal (`developer.husqvarnagroup.cloud/apis/automower-connect-api`)
  is a JS SPA — `WebFetch` only returns the page title, no usable content.
  Ground truth came from live `curl` calls against the real API instead.

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

(Authoritative live copy is `mowers.json`, regenerated by `am list`.)
