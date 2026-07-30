# Configuration / generated files / security

## Config and generated files

Both live in the repo root, resolved at runtime by walking up from the built
executable to the nearest `.slnx` — not `bin/`, so a `dotnet clean` (which
wipes `bin/`/`obj/`) never touches either of them. Both are gitignored.

| Path | Contents |
|---|---|
| `.config/config.json` | App key/secret + `track`/`hybrid-track` interval settings (via `config`) |
| `.data/state.json` | The active mower selection (from `use`) - unaffected by the SQLite migration, still a plain file |
| `.data/common.db` | Mower registry (from `list`) - see [`database-schema.md`](database-schema.md) |
| `.data/mower-<mower name>.db` | One SQLite db per mower - raw + derived history, cached schedule, daily statistics. See [`database-schema.md`](database-schema.md) for the full schema |

**Legacy JSONL files** (`.data/mowers.json`, `.data/schedule-<mower>.json`,
`.data/track-<mower>.jsonl`, `.data/events-<mower>.jsonl`,
`.data/statistics-<mower>.jsonl`) predate the 2026-07-30 SQLite cutover -
kept on disk as a historical record, but nothing reads or writes them
anymore.

## Security note

`.config/config.json` contains your Husqvarna app key and secret in plain
text. It's already gitignored — don't remove that entry, and don't commit
the file directly. `config.example.json` (repo root, tracked) is the
placeholder template to copy from if you ever need to recreate it by hand;
`config AppKey=... AppSecret=...` does the same thing without manual editing.

`AutomowerWeb`'s public deployment is intentionally unauthenticated — see
[`deployment.md`](deployment.md) for why the exposed data is considered
low-stakes, and the `basicauth` upgrade path if that ever changes.
