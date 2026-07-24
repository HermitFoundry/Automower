#!/usr/bin/env bash
# Restores +x on this repo's shell scripts - needed after 'git pull'/clone
# because they were committed from a Windows checkout (where git either
# can't see the executable bit at all, or defaults new files to non-
# executable), so a Linux checkout gets them back as plain files no matter
# how many times 'chmod +x' was already run against a previous pull.
# Re-run this after every pull that touches a *.sh file. No root needed -
# you already own your own checkout.
set -euo pipefail

dir="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
chmod +x "$dir"/*.sh

echo "Marked executable:"
for f in "$dir"/*.sh; do
    echo "  $(basename "$f")"
done
