# Automower

A home automation project for a Husqvarna Automower account: a public,
read-only web dashboard, a CLI, a WebSocket-event-driven tracker, and a
SQLite-backed history store, all running unattended on a QNAP NAS. What
started as a small console client for the Husqvarna Automower Connect API
has grown into a small system — this section is the map; everything else
lives in short sections below with links to the full detail in `docs/`.

## What this is

- **[`AutomowerWeb`](#web-dashboard-automowerweb)** — a public, read-only
  Blazor Server dashboard: live status for every mower, per-mower history,
  work areas, schedule, and lifetime statistics. This is what you'd point a
  browser at.
- **`AutomowerConsole`** — the CLI (`am.cmd`/`am.sh`) for everything the
  dashboard doesn't cover: setting config, one-off status checks, and
  starting the trackers. See [`docs/cli-usage.md`](docs/cli-usage.md).
- **`hybrid-track`** — the default tracker: subscribes to Husqvarna's
  WebSocket event-push API for near-instant status changes, with a slow
  REST-refresh loop underneath for the statistics/schedule fields events
  don't carry. Runs continuously, one process per mower.
- **SQLite storage** — every mower gets its own database (raw event log +
  a derived, queryable history), plus one shared database for the mower
  registry. See [`docs/database-schema.md`](docs/database-schema.md).
- **`AutomowerConsole.Core`** — the shared domain/service layer both the
  CLI and the web app sit on top of. See [`docs/design.md`](docs/design.md).

All four mower-facing pieces (`AutomowerWeb` and 3 `hybrid-track` daemons,
one per mower) run as separate long-lived processes on a QNAP NAS — see
**Deployment** below.

## Web dashboard (`AutomowerWeb`)

A read-only Blazor Server app: a `/` dashboard (live status per mower —
activity, battery, work area, connected, next start, location, weather —
plus that mower's sessions from *today only* and a 7-day rollup) and a
`/mower/{name}` details page per mower (the same status facts up top, plus
full session history, daily rollup, work areas, stay-out zones, schedule,
recent messages, settings/capabilities, and lifetime operation statistics
at the bottom). No login, and deliberately no mower control anywhere in it
— an unauthenticated public control surface for a physical outdoor device
is a different risk class than an unauthenticated read-only dashboard, and
hasn't been asked for.

**Location and weather** are derived from each mower's own latest GPS
position, via two free, keyless external services called server-side:
OpenStreetMap's **Nominatim** for reverse geocoding and **Open-Meteo** for
current weather. `AutomowerWeb` needs outbound internet access to those two
hosts in addition to Husqvarna's own API.

Run it locally from the repo root, so it can see `.config`/`.data`:

```
dotnet run --project AutomowerWeb
```

then open the URL it prints (default `http://localhost:5152`). On the QNAP
deployment it runs via `./startweb.sh` in a detached tmux session instead —
see [`docs/web-dashboard.md`](docs/web-dashboard.md) for that, dev mode
(`startweb.dev`), why there's no auto-refresh timer, and the external
service caching behavior.

## CLI

Everything the dashboard doesn't cover — setting up credentials, one-off
status/message/schedule checks, and starting a tracker by hand — goes
through the CLI: `am.cmd <command>` (Windows) / `./am.sh <command>`
(Linux/macOS), or `dotnet run -- <command>` directly. See
**[`docs/cli-usage.md`](docs/cli-usage.md)** for the full command
reference and examples.

## Installation / setup

Needs the .NET 10 SDK and a Husqvarna Developer Portal app key/secret; a
`./bootstrap.sh` script provisions a fresh Linux container end-to-end. See
**[`docs/installation.md`](docs/installation.md)** for prerequisites and
first-run setup (`dotnet build`, then `config AppKey=... AppSecret=...`).

## Design

Four projects under `automower.slnx` — a shared domain/service layer
(`AutomowerConsole.Core`), the CLI, its tests, and the web app — plus the
SQLite storage architecture (per-mower raw-event + derived-observation
databases) behind the `IMowerRepository` abstraction. See
**[`docs/design.md`](docs/design.md)** for the full project layout and
storage design.

## Configuration / reference

Where config and generated data live (`.config/config.json`, the per-mower
and common SQLite databases under `.data/`), and the security note on the
gitignored credentials file. See
**[`docs/configuration.md`](docs/configuration.md)**.

## Deployment

Runs unattended on a QNAP TS-673A NAS: one container running the 3
`hybrid-track` daemons in tmux, a separate container for `AutomowerWeb`,
and a Caddy container fronting it with automatic TLS for the public
dashboard at `https://Terje-TS673A.myqnapcloud.com/`. See
**[`docs/deployment.md`](docs/deployment.md)** for the public deployment
architecture, and **[`docs/qnap-access.md`](docs/qnap-access.md)** /
**[`.claude/skills/qnap-ops/SKILL.md`](.claude/skills/qnap-ops/SKILL.md)**
for getting a shell on the containers and redeploying.

## Documentation index

| Doc | Covers |
|---|---|
| [`docs/installation.md`](docs/installation.md) | Prerequisites, first-run setup |
| [`docs/cli-usage.md`](docs/cli-usage.md) | Full CLI command reference and examples |
| [`docs/web-dashboard.md`](docs/web-dashboard.md) | `AutomowerWeb` deep dive — external services, dev mode, QNAP tmux operation |
| [`docs/design.md`](docs/design.md) | Project layout, service breakdown, storage architecture |
| [`docs/configuration.md`](docs/configuration.md) | Config/generated file locations, security note |
| [`docs/tracking.md`](docs/tracking.md) | `hybrid-track`/`track` polling & event design, `sessions`/`daily`/`seasons`, `calendar` vs `planner`, running unattended |
| [`docs/database-schema.md`](docs/database-schema.md) | SQLite storage backend schema (mermaid ER diagrams) |
| [`docs/deployment.md`](docs/deployment.md) | Public deployment architecture (Caddy, TLS, hostname, no-auth decision) |
| [`docs/qnap-access.md`](docs/qnap-access.md) | Getting a shell on the QNAP container over SSH, SSH-tunnel testing |
| [`docs/qnap_infrastructure_setup.md`](docs/qnap_infrastructure_setup.md) | Deeper QNAP/Container Station operational notes (timezone, port mapping, SSH forwarding) |
| [`.claude/skills/automower-api/SKILL.md`](.claude/skills/automower-api/SKILL.md) | API implementation notes — auth flow, endpoint quirks, timestamp units, WebSocket research |
| [`.claude/skills/qnap-ops/SKILL.md`](.claude/skills/qnap-ops/SKILL.md) | QNAP operational playbook — SSH/docker patterns, redeploy commands |
| [`.claude/plans/sqlite-and-hybrid-tracking-migration.md`](.claude/plans/sqlite-and-hybrid-tracking-migration.md) | Archived plan for the SQLite + hybrid-tracking migration and 2026-07-30 cutover |
