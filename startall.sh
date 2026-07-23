#!/usr/bin/env bash
# Starts one detached tmux session per mower on this account, each running
# 'track' for that mower - see README's "Running track unattended" section
# for why tmux (survives SSH/docker exec disconnects) and why per-mower
# sessions (track only handles one mower per process).
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

if [ ! -f .data/mowers.json ]; then
    echo "No cached mower list found - fetching..."
    ./am.sh list
fi

mapfile -t names < <(grep -o '"Name": *"[^"]*"' .data/mowers.json | sed -E 's/"Name": *"([^"]*)"/\1/')

if [ ${#names[@]} -eq 0 ]; then
    echo "No mowers found in .data/mowers.json - run './am.sh list' first." >&2
    exit 1
fi

for name in "${names[@]}"; do
    short="${name%% *}"
    session="automower-$short"

    if tmux has-session -t "$session" 2>/dev/null; then
        echo "  $session already running, skipping"
        continue
    fi

    tmux new-session -d -c "$dir" -s "$session" "$dir/am.sh" track "$short"
    echo "  started $session (track $short)"
done

echo "Done. 'tmux ls' to see them; 'tmux attach -t <name>' to check on one; ./stopall.sh to stop them all."
