# Installation / setup

## Prerequisites

- .NET 10 SDK
- A Husqvarna Developer Portal application (https://developer.husqvarnagroup.cloud/)
  subscribed to both the **Authentication API** and the **Automower Connect API**,
  giving you an application (app) key and secret
- On a fresh Linux container/host (e.g. a new QNAP Container Station
  container): `git`, `curl`, `tmux` (for `startall.sh`/`stopall.sh`), and the
  system timezone set to match the mowers' own configured local time (not
  the container default, often UTC — see
  [`tracking.md`](tracking.md) for why this matters). Run
  `./bootstrap.sh` as root to install all of the above plus the .NET SDK in
  one idempotent pass.

## Setup

1. Build once to restore/compile:

   ```
   dotnet build
   ```

2. Set your credentials with the `config` command (creates `.config/config.json`
   if it doesn't exist yet):

   ```
   dotnet run -- config AppKey=your-app-key AppSecret=your-app-secret
   ```

   `config` also accepts any other config field the same way (see
   [`tracking.md`](tracking.md) for the full list, e.g.
   `IdleIntervalSeconds=240`). Run `dotnet run -- config` with no arguments to
   print the current values (secrets masked). `config.example.json` (repo
   root, tracked) documents the full field set as a reference.

   The config file lives in `.config/config.json`, and `list`/`use`/`track`
   generate state in `.data/` — both are resolved relative to the repo root
   (found by walking up from the built executable to the nearest `.slnx`),
   not the `bin/` build output folder, so `dotnet clean` never touches them.
   Both directories are gitignored — keep it that way (see
   [`configuration.md`](configuration.md#security-note)).
   `AutomowerWeb` (see the README's **Web dashboard** section) reads the same
   two directories/db files, so it needs to run somewhere that can see them
   too.

On Linux/macOS, `am.sh`/`startall.sh`/`stopall.sh`/`bootstrap.sh` need the
executable bit, which git now tracks directly (`git update-index --chmod=+x`
was applied and committed) — a fresh `git clone` on Linux gets it for free.
If an *existing* checkout still loses it after a pull (some git configs,
e.g. `core.fileMode=false`, won't restore local bits from the index), run
`./fix-permissions.sh` to reset all of this repo's `*.sh` scripts at once,
or `chmod +x am.sh` for just the one. If you'd rather not chmod anything,
`bash am.sh <command>` works too without it.

Once installed, see [`cli-usage.md`](cli-usage.md) for running commands, or
the README's **Web dashboard** section to run `AutomowerWeb` instead.
