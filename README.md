# AutomowerConsole

A small C# console client for the Husqvarna Automower Connect API. Authenticates
with an app key/secret via OAuth2 client-credentials, then lets you inspect
mower status, messages, work areas, stay-out zones, and schedules, plus an
adaptive polling/logging mode (`track`).

## Prerequisites

- .NET 10 SDK
- A Husqvarna Developer Portal application (https://developer.husqvarnagroup.cloud/)
  subscribed to both the **Authentication API** and the **Automower Connect API**,
  giving you an application (app) key and secret

## Setup

1. Build once to restore/compile:

   ```
   dotnet build
   ```

2. Set your credentials with the `config` command (creates `config.json` if it
   doesn't exist yet):

   ```
   dotnet run -- config AppKey=your-app-key AppSecret=your-app-secret
   ```

   `config` also accepts any other `config.json` field the same way (see
   **`track`: adaptive polling and logging** below for the full list, e.g.
   `IdleIntervalSeconds=240`). Run `dotnet run -- config` with no arguments to
   print the current values (secrets masked). `config.example.json` documents
   the full field set as a reference.

   `config.json` holds a live secret — keep it out of source control (it's
   already in `.gitignore`; see **Security note** below).

   ```
   dotnet build
   ```

## Running

Either:

```
dotnet run -- <command> [args]
```

or use the `am.cmd` shortcut, which forwards all arguments the same way:

```
am <command> [args]
```

## Commands

Commands that act on "the active mower" (the one set via `use`) accept an
optional trailing `[mower]` argument — a name, id, or list index — to target
a different mower for just that one call, without changing the active
selection.

| Command | Description |
|---|---|
| `config` | Show current `config.json` values (secrets masked) |
| `config Key=Value ...` | Set one or more `config.json` values, e.g. `config AppKey=xxx AppSecret=yyy` |
| `list` | Fetch and list all mowers on the account, save to `mowers.json` |
| `use <name\|id\|index>` | Set the active mower (stored in `state.json`) |
| `current` | Show the currently active mower |
| `status [--all] [mower]` | Show current status; `--all` dumps the full raw JSON payload |
| `messages [mower]` | Show message/error history, newest first, with human-readable descriptions |
| `errorcodes` | Show the full error code → description table |
| `workareas [mower]` | List all work areas |
| `workarea <name\|id> [mower]` | Detailed info for one work area, including its schedule |
| `stayoutzones [mower]` | List configured stay-out zones |
| `schedule [mower]` | Show and refresh the cached schedule (`schedule.json`) |
| `track [seconds] [mower]` | Adaptive-interval polling with logging to `track.jsonl` (see below) |
| `help` | Show usage |

### Examples

```
am list
am use "AM430X NERA"
am status
am status "AM405X"          # check a different mower without switching active
am workarea nederside
am messages
am track                    # start adaptive polling for the active mower
```

## `track`: adaptive polling and logging

`track` polls the mower's full status on an interval and appends one JSON
line per kept poll to `track.jsonl`, so you can see exactly how much data a
day of monitoring costs (the log file's size on disk is the answer). Each
line is `{timestamp, mowerId, mowerName, bytes, response}`, where `response`
is the complete raw API payload for that poll.

The polling interval adapts to what's actually happening, in this priority
order:

1. **Active or in a scheduled mowing window** — poll fast (default 60s).
   This covers the mower actually being out mowing, and also the window
   where it's scheduled to start but might still be charging (charge
   duration isn't predictable, so we poll fast to catch the exact moment
   it leaves).
2. **Nighttime** (default 22:00–08:00) and otherwise idle — poll every
   30 minutes, since no one manually starts a mow overnight.
3. **Daytime, idle, not scheduled** — poll every 5 minutes, watching for a
   manually-started mow. If one starts, the next poll notices immediately
   and switches to the fast interval.

While the mower is parked at the charging station and none of the above
applies, only the *first* poll after arrival is logged — repeat polls while
still parked are skipped (only printed to the console), so idle time at the
dock doesn't inflate the log.

All intervals, plus the nighttime window, are configurable in `config.json`
(defaults shown):

```json
{
  "ScheduledIntervalSeconds": 60,
  "IdleIntervalSeconds": 300,
  "NightIntervalSeconds": 1800,
  "NightStartHour": 22,
  "NightEndHour": 8
}
```

The schedule used to detect "scheduled window" comes from `schedule.json`,
refreshed for free from every `track` poll (the mower payload already
includes the calendar — no extra API call). Run `schedule [mower]` on its
own to force a refresh without starting `track`.

Press Ctrl+C to stop tracking; already-written log lines are never lost
since each poll is flushed to disk immediately.

## Generated files

These are created at runtime next to the built executable, not checked in:

| File | Contents |
|---|---|
| `mowers.json` | Cached list of mowers on the account (from `list`) |
| `state.json` | The active mower selection (from `use`) |
| `schedule.json` | Cached per-mower calendar, keyed by mower id (from `schedule` or `track`) |
| `track.jsonl` | Append-only log of polls from `track` |

## Security note

`config.json` contains your Husqvarna app key and secret in plain text.
Don't commit it — add it to `.gitignore` and consider keeping a
`config.example.json` template with placeholder values instead.

## Project layout

- `Program.cs` — CLI entry point and command implementations
- `HusqvarnaClient.cs` — OAuth2 authentication and Automower Connect API calls
- `Models.cs` — JSON response models and config/cache record types
- `Storage.cs` — reads/writes `config.json`, `mowers.json`, `state.json`, `schedule.json`
- `ErrorCodes.cs` — full Automower error code → description table
- `am.cmd` — shortcut that forwards arguments to `dotnet run`

For API implementation notes (auth flow, endpoint quirks, timestamp units,
external references) see `.claude/skills/automower-api/SKILL.md`.
