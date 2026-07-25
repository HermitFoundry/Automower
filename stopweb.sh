#!/usr/bin/env bash
# Gracefully stops the automowerweb tmux session (see startweb.sh): sends
# Ctrl+C so the ASP.NET host shuts down cleanly, then force-kills the tmux
# session if it's still up a few seconds later. Mirrors stopall.sh's
# graceful-then-force pattern for the per-mower track sessions.
set -uo pipefail

session="automowerweb"

if ! tmux has-session -t "$session" 2>/dev/null; then
    echo "$session is not running."
    exit 0
fi

echo "Stopping $session..."
tmux send-keys -t "$session" C-c

sleep 3

if tmux has-session -t "$session" 2>/dev/null; then
    echo "  didn't stop gracefully in time - force-killing"
    tmux kill-session -t "$session"
else
    echo "  stopped cleanly"
fi
