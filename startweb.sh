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
tmux new-session -d -c "$dir" -s "$session" bash -c "dotnet '$dll' --urls http://0.0.0.0:$port > '$log' 2>&1"

echo "Started $session on port $port (log: $log)."
echo "'tmux attach -t $session' to check on it; ./stopweb.sh to stop it."
