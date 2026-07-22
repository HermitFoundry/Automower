#!/usr/bin/env bash
# One-time migration for existing checkouts:
#   1. moves config.json and the runtime-generated mowers/state/schedule.json
#      out of bin/ (wiped by 'dotnet clean') into .config/ and .data/, which
#      the app now reads/writes instead.
#   2. splits any old combined bin/**/track.jsonl (from before track logs
#      became per-mower) into .data/track-<mower name>.jsonl files, using
#      each line's own "mowerName" field (via awk, no extra dependency).
# Safe to re-run: skips/merges rather than overwriting anything already in place.
set -euo pipefail

script_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
cd "$script_dir"

mkdir -p .config .data

migrate_single() {
    local name="$1" dest="$2"
    [ -d bin ] || return 0
    local src
    src=$(find bin -name "$name" -print -quit 2>/dev/null || true)
    [ -n "$src" ] || return 0

    if [ -f "$dest" ]; then
        echo "  skip: $dest already exists - leaving $src as-is (compare manually if needed)"
    else
        mv "$src" "$dest"
        echo "  moved $src -> $dest"
    fi
}

split_track_jsonl() {
    local src="$1" data_dir="$2"
    [ -f "$src" ] || return 0

    if command -v awk >/dev/null 2>&1; then
        awk -v data_dir="$data_dir" '
            {
                line = $0
                name = "unknown"
                if (match(line, /"mowerName":"[^"]*"/)) {
                    # strip the 13-char `"mowerName":"` prefix and closing quote
                    name = substr(line, RSTART + 13, RLENGTH - 14)
                }
                gsub(/[^A-Za-z0-9_-]/, "-", name)
                while (name ~ /--/) gsub(/--/, "-", name)
                gsub(/^-+/, "", name)
                gsub(/-+$/, "", name)
                if (name == "") name = "unknown"

                dest = data_dir "/track-" name ".jsonl"
                print line >> dest
                count[dest]++
            }
            END {
                for (d in count) print "  split " count[d] " record(s) into " d
            }
        ' "$src"
        rm "$src"
    else
        echo "  awk not found - can't auto-split $src by mower; moved as-is to $data_dir/track-unsplit.jsonl for manual handling"
        mv "$src" "$data_dir/track-unsplit.jsonl"
    fi
}

echo "Migrating leftover state from bin/ into .config/ and .data/ ..."

migrate_single config.json .config/config.json
migrate_single mowers.json .data/mowers.json
migrate_single state.json .data/state.json
migrate_single schedule.json .data/schedule.json

if [ -d bin ]; then
    track_src=$(find bin -name track.jsonl -print -quit 2>/dev/null || true)
    split_track_jsonl "$track_src" "$script_dir/.data"
fi

echo "Done. Run 'dotnet build' and 'am.sh config' (or 'dotnet run -- config') to verify."
