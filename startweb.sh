#!/usr/bin/env bash
# Starts AutomowerWeb in a detached tmux session (survives SSH/docker exec
# disconnects - same reasoning as startall.sh's per-mower track sessions),
# bound to all interfaces so it's reachable from outside the container - see
# README's "Web dashboard" section. Builds once, then runs the compiled
# .dll directly rather than 'dotnet run' - same reasoning as am.sh: 'dotnet
# run' doesn't reliably forward SIGINT to the process it spawns, which would
# break a graceful Ctrl+C stop via stopweb.sh.
#
# Session name deliberately does NOT start with "automower-" (unlike the
# per-mower track sessions, "automower-AM430X" etc.) so startall.sh/
# stopall.sh's '^automower-' tmux session matching never picks this one up
# by accident - starting/stopping the dashboard is a separate concern from
# starting/stopping the mowers' track loops, and shouldn't get bundled into
# "stop everything" by a naming coincidence.
#
# ASPNETCORE_ENVIRONMENT=Development, deliberately: without it, this
# defaults to Production, and .NET 9+'s MapStaticAssets() static-file
# pipeline serves every asset (app.css, the Blazor JS) as a 200 OK with an
# empty body - it expects publish-time asset processing (compression,
# manifest) that a plain 'dotnet build' never generates, and Production
# mode has no fallback for that; Development mode serves straight from
# wwwroot instead and doesn't need it. Confirmed by direct repro: identical
# launch command, only the environment variable differed, empty body vs a
# real file both times. TODO before this is ever exposed beyond a LAN/SSH
# tunnel: Development mode also enables the detailed exception page, which
# would leak stack traces to the internet - the real fix then is
# 'dotnet publish' + running the published output, not this env var, but
# that's a bigger change deferred until public exposure is actually being
# set up (see README's "Not yet deployed anywhere").
set -euo pipefail

dir="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
cd "$dir"
session="automowerweb"
port="${1:-5152}"

if ! command -v tmux >/dev/null 2>&1; then
    echo "tmux not found. Install it first, e.g.: apt-get update && apt-get install -y tmux" >&2
    exit 1
fi

if tmux has-session -t "$session" 2>/dev/null; then
    echo "$session is already running - run ./stopweb.sh first if you want to restart it with freshly built code."
    exit 0
fi

dll="$dir/AutomowerWeb/bin/Debug/net10.0/AutomowerWeb.dll"
dotnet build "$dir/AutomowerWeb/AutomowerWeb.csproj" -v quiet

mkdir -p "$dir/.data"
log="$dir/.data/startweb.log"
tmux new-session -d -c "$dir" -s "$session" bash -c "ASPNETCORE_ENVIRONMENT=Development dotnet '$dll' --urls http://0.0.0.0:$port > '$log' 2>&1"

# 'tmux new-session -d' returns immediately, before the app has actually
# bound its port - a crash (e.g. "address already in use" from a leftover
# process holding $port, which is exactly what bit us the first time this
# happened) closes the pane almost instantly, and a naive "Started" message
# printed right after would be a lie. Poll briefly for either a clean
# startup or the session dying, so a failure is reported here instead of
# only discoverable later via a manual 'tmux ls'/log check.
started=false
for _ in $(seq 1 10); do
    if ! tmux has-session -t "$session" 2>/dev/null; then
        break
    fi
    if grep -q "Now listening on" "$log" 2>/dev/null; then
        started=true
        break
    fi
    sleep 1
done

if [ "$started" = true ]; then
    echo "Started $session on port $port (log: $log)."
    echo "'tmux attach -t $session' to check on it; ./stopweb.sh to stop it."
else
    echo "AutomowerWeb failed to start (crashed, or didn't bind within 10s) - last log lines:" >&2
    echo "---" >&2
    tail -20 "$log" >&2
    echo "---" >&2

    if grep -qi "address already in use" "$log" 2>/dev/null; then
        # '[A]utomowerweb' rather than 'automowerweb' + a separate 'grep -v
        # grep' - the bracket trick keeps grep's own invocation from
        # matching itself, same idea, one less pipeline stage. Matches by
        # command-line content (works whether the culprit is a bare
        # 'dotnet run --project AutomowerWeb' or the compiled binary
        # directly) rather than needing 'ss'/'lsof', which may not be
        # installed on a minimal container.
        echo "Port $port is already held by another process - probably a leftover" >&2
        echo "'dotnet run --project AutomowerWeb' from manual testing, not this script" >&2
        echo "(the tmux session above already crashed and closed itself)." >&2
        echo >&2
        stale=$(ps -ef | grep -i "[A]utomowerweb")
        if [ -n "$stale" ]; then
            echo "Processes to kill:" >&2
            echo "$stale" >&2
            echo >&2
            echo "  kill $(echo "$stale" | awk '{print $2}' | tr '\n' ' ')" >&2
            echo "(add -9 if plain kill doesn't stop them within a few seconds), then re-run ./startweb.sh." >&2
        else
            echo "Couldn't find it via 'ps -ef | grep -i automowerweb' - check 'ss -tlnp' or" >&2
            echo "'netstat -tlnp' for whatever's actually bound to port $port." >&2
        fi
    fi

    echo >&2
    echo "Full log: $log" >&2
    exit 1
fi
