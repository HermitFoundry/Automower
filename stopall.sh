#!/usr/bin/env bash
# Gracefully stops every automower-* tmux session started by startall.sh:
# sends Ctrl+C into each one so 'track' exits cleanly (prints its "Stopped..."
# summary, same as stopping it by hand - see README), then gives it a moment
# and force-kills anything still around (tmux's own session, not just the
# process, since 'track' exiting normally already closes its session on its
# own - see README's "Deleting a tmux session"). Log data is never at risk
# either way, since 'track' flushes each poll to disk immediately.
set -uo pipefail

sessions=$(tmux ls -F '#{session_name}' 2>/dev/null | grep '^automower-' || true)

if [ -z "$sessions" ]; then
    echo "No automower-* tmux sessions running."
    exit 0
fi

echo "Stopping:"
while IFS= read -r session; do
    echo "  $session"
    tmux send-keys -t "$session" C-c
done <<< "$sessions"

sleep 3

while IFS= read -r session; do
    if tmux has-session -t "$session" 2>/dev/null; then
        echo "  $session didn't stop gracefully in time - force-killing"
        tmux kill-session -t "$session"
    else
        echo "  $session stopped cleanly"
    fi
done <<< "$sessions"

echo "Done."
