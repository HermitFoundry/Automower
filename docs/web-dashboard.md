# Web dashboard (`AutomowerWeb`)

A read-only Blazor Server app: a `/` dashboard (live status per mower —
activity, battery, work area, connected, next start, location, weather —
plus that mower's sessions from *today only* and a 7-day rollup) and a
`/mower/{name}` details page per mower (the same status facts up top, plus
full session history, daily rollup, work areas, stay-out zones, schedule,
recent messages, settings/capabilities, and lifetime operation statistics
at the bottom). No login yet, and deliberately no mower control anywhere in
it — an unauthenticated public control surface for a physical outdoor
device is a different risk class than an unauthenticated read-only
dashboard, and hasn't been asked for.

**Location and weather** are derived from each mower's own latest GPS
position (`positions[0]` in the API response - absent for a mower with no
GPS fix, in which case those two rows are just omitted). Two free, keyless
external services, called server-side: OpenStreetMap's **Nominatim** for
reverse geocoding (place name cached indefinitely per mower - a charging
station doesn't move meter-to-meter between polls) and **Open-Meteo** for
current weather (cached 20 minutes, since it actually changes). This means
`AutomowerWeb` needs outbound internet access to those two hosts, in
addition to Husqvarna's own API - true for local dev, and something to keep
in mind once it's running somewhere with more restricted egress.

Run it locally the same way as any ASP.NET project, from the repo root so
it can see `.config`/`.data`:

```
dotnet run --project AutomowerWeb
```

then open the URL it prints (default `http://localhost:5152`).

**On the QNAP container, run it via `startweb.sh`/`stopweb.sh`** instead of
a plain `dotnet run` you'd have to babysit in a terminal — same pattern as
`startall.sh`/`stopall.sh` for `track`: a detached tmux session that
survives an SSH disconnect, publishing once (`dotnet publish -c Release`,
not `dotnet build` — see the comment in `startweb.sh` for why: this app
needs a real physically-copied `wwwroot`, which only `publish` produces)
and running the published `.dll` directly (not `dotnet run`, for the same
graceful-Ctrl+C-forwarding reason as `am.sh`). Bound to all interfaces
(`0.0.0.0:5152` by default, `./startweb.sh <port>` to override) so it's
reachable from outside the container, not just `localhost`:

```
./startweb.sh          # publishes (Release), starts in tmux session "automowerweb"
./stopweb.sh            # graceful stop (Ctrl+C, falls back to force-kill)
```

**`startweb.dev`/`stopweb.dev`** are the same thing in
`ASPNETCORE_ENVIRONMENT=Development` (a real exception page instead of a
generic error — useful while debugging; `-c Debug` publish, faster than
Release) instead of Production. Same tmux session name as `startweb.sh` —
they're two alternate ways to run the same app, not meant to run at once.
**Not safe to expose beyond a LAN/SSH tunnel** — Development mode leaks
stack traces on any unhandled exception.

**If you rebuild/pull new code, `startweb.sh` won't pick it up on its
own** — a tmux session that's already running keeps whatever was loaded in
memory when it started, same as any other long-running process here (see
`track`'s equivalent gotcha in `docs/tracking.md`). Run `./stopweb.sh` then
`./startweb.sh` to actually restart it on the new build; `./startweb.sh`
alone just says "already running" and does nothing if a session already
exists under that name.

**No auto-refresh timer on the dashboard, by design.** It's a 4th
independent process authenticating with the same Husqvarna app key/secret
as the 3 `track` sessions (see `docs/tracking.md`'s `startall.sh` notes on
Husqvarna's `simultaneous.logins` rejection) — a background poll loop would
add another recurring source of auth traffic for a dashboard nobody's
continuously watching. It loads once per page visit and on an explicit
"🔄 Refresh" click instead.
