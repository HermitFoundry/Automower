#!/usr/bin/env bash
# Starts one detached tmux session per mower on this account, each running
# 'track' for that mower - see README's "Running track unattended" section
# for why tmux (survives SSH/docker exec disconnects) and why per-mower
# sessions (track only handles one mower per process).
#
# Builds once, up front, and each tmux session runs the built .dll directly
# rather than going through am.sh - am.sh runs 'dotnet build' on every
# invocation, and since 'tmux new-session -d' returns immediately (it
# doesn't wait for the launched command to finish), a tight loop starting
# one am.sh per mower can fire off several concurrent builds against the
# same project. If two race for the same obj/bin output files, one can fail
# to build; am.sh's 'set -e' then stops before ever exec'ing 'track', and
# since that was the only process in the pane, its tmux session closes
# itself almost immediately after being created - looks like "only one
# mower started" even though the loop reported starting all of them.
#
# Uses just the model prefix of each mower's name (the part before the first
# space, e.g. "AM430X" out of "AM430X NERA") as both the tmux session suffix
# and the mower query passed to 'track' - shorter, and relies on the CLI's
# existing name-contains matching (see MowerService.FindMower) to resolve it
# back to the full mower. Only safe while each model prefix is unique across
# the account (true for the current 3 mowers - one of each model); if a
# second mower ever shares a prefix, this would need to fall back to the
# full name or the mower id instead.
# Session names: automower-<model prefix>, e.g. automower-AM430X. Safe to
# re-run: skips any mower whose session is already up rather than starting
# a duplicate.
set -euo pipefail

dir="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
cd "$dir"

if ! command -v tmux >/dev/null 2>&1; then
    echo "tmux not found. Install it first, e.g.: apt-get update && apt-get install -y tmux" >&2
    exit 1
fi

dll="$dir/AutomowerConsole/bin/Debug/net10.0/AutomowerConsole.dll"
dotnet build "$dir/AutomowerConsole/AutomowerConsole.csproj" -v quiet

if [ ! -f .data/mowers.json ]; then
    echo "No cached mower list found - fetching..."
    dotnet "$dll" list
fi

mapfile -t names < <(grep -o '"Name": *"[^"]*"' .data/mowers.json | sed -E 's/"Name": *"([^"]*)"/\1/')

if [ ${#names[@]} -eq 0 ]; then
    echo "No mowers found in .data/mowers.json - run './am.sh list' first." >&2
    exit 1
fi

mkdir -p "$dir/.data"

for name in "${names[@]}"; do
    short="${name%% *}"
    session="automower-$short"

    if tmux has-session -t "$session" 2>/dev/null; then
        echo "  $session already running, skipping"
        continue
    fi

    # stdout+stderr redirected to a per-mower log - if 'track' crashes fast
    # (bad auth, an unhandled exception, ...) the tmux pane closes almost
    # instantly (it's the only process in it), too fast to attach and read
    # anything live. The log survives that, so the failure is diagnosable
    # after the fact with 'cat .data/startall-<short>.log' instead of having
    # to reproduce it by running the same command in the foreground by hand.
    # Deliberately not using 'tee'/keeping the pane open after exit - that
    # would also change what a *clean* stop looks like, breaking stopall.sh's
    # "closes itself within 3s" detection for a graceful Ctrl+C stop.
    log="$dir/.data/startall-$short.log"
    tmux new-session -d -c "$dir" -s "$session" bash -c "dotnet '$dll' track '$short' > '$log' 2>&1"
    echo "  started $session (track $short, log: $log)"
done

echo "Done. 'tmux ls' to see them; 'tmux attach -t <name>' to check on one; ./stopall.sh to stop them all."
