# Design / project layout

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
  - `HybridTrackingService.cs` — the WebSocket-event-driven tracker
    (`hybrid-track`): consumes `MowerEventStream` for near-instant status,
    plus a slow REST-refresh loop for statistics/schedule
  - `MowerEventStream.cs` — shared WebSocket connect/reconnect/message-framing
    plumbing, used by both `HybridTrackingService` and the standalone
    `EventTrackingService`
  - `MowerRepository.cs` / `SqliteMowerRepository.cs` / `MowerRegistry.cs` —
    the `IMowerRepository`/`IMowerRegistry` storage abstraction and its
    SQLite-backed implementation (see [`database-schema.md`](database-schema.md))
  - `ErrorCodes.cs`, `Extensions.cs` (`FormatDuration`, `IsNighttime`) — small
    public helpers both consumers use for display
  - `AutomowerConnect.cs` / `HusqvarnaClient.cs` — auth + raw HTTP calls,
    deliberately kept `internal` to Core — nothing outside Core, in either
    the CLI or the web app, should reach the API directly; go through the
    services above instead
  - `Storage.cs` — reads/writes `.config/config.json`, resolves per-mower/
    common SQLite db paths, and finds the repo root (nearest `.slnx`, not
    `.csproj` — there's only ever one, and it stays in the true repo root
    regardless of how many projects sit under it) that they're anchored to.
    `public`, unlike the other internals above, since the CLI's own
    config/state commands (`config`, `use`, `current`) call it directly with
    no service layer of their own
  - `Models.cs` — JSON response models and config/cache record types (the
    pure wire-DTOs the API's JSON unwraps into stay `internal`; the actual
    domain types services return are `public`)
- **`AutomowerConsole/`** — the CLI. Just `Program.cs` now: argument
  parsing and result printing on top of `AutomowerConsole.Core`'s services
- **`AutomowerConsole.Tests/`** — NUnit tests, referencing
  `AutomowerConsole.Core` directly (it's what they've always actually
  tested — `TrackingService`, etc.). Run with `dotnet test`.
- **`AutomowerWeb/`** — the Blazor web dashboard, see the README's
  **Web dashboard** section

## Scripts

- `am.cmd` / `am.sh` — shortcuts that build `AutomowerConsole.csproj` once
  and then run the compiled `.dll` directly (not `dotnet run` — see
  [`cli-usage.md`](cli-usage.md))
- `startall.sh` / `stopall.sh` — start/stop one tmux `hybrid-track` session
  per mower (see [`tracking.md`](tracking.md))
- `startweb.sh` / `stopweb.sh` — start/stop `AutomowerWeb` in a detached
  tmux session (see [`web-dashboard.md`](web-dashboard.md))
- `bootstrap.sh` / `fix-permissions.sh` — one-time container provisioning
  and the `chmod +x` fallback (see [`installation.md`](installation.md))
- `automower.slnx` — the solution file referencing all four projects

## Storage architecture

Each mower gets its own SQLite database (`RawEvents` — the unprocessed
source of truth for every REST poll and WebSocket event — plus a derived,
sparse `Observations` table, `DailyStatistics`, and `Schedule`), and there's
one common database (`Mowers` registry). `IMowerRepository`/`IMowerRegistry`
are the storage seam — nothing outside `SqliteMowerRepository`/
`SqliteMowerRegistry` knows it's SQLite underneath, which is what made the
2026-07-30 cutover from JSONL a swap-the-implementation change rather than a
rewrite. Full schema and the rationale behind the raw/derived split live in
[`database-schema.md`](database-schema.md); the original migration plan
(including the JSONL→SQLite verification steps) is archived at
[`../.claude/plans/sqlite-and-hybrid-tracking-migration.md`](../.claude/plans/sqlite-and-hybrid-tracking-migration.md).
