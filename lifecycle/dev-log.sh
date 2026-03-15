#!/usr/bin/env bash
# dev-log.sh — View recent dev server logs.
#
# Usage:
#   ./lifecycle/dev-log.sh          # Last 50 lines
#   ./lifecycle/dev-log.sh -f       # Follow (tail -f)
#   ./lifecycle/dev-log.sh 100      # Last 100 lines

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
LOG_FILE="$(cd "$SCRIPT_DIR/.." && pwd)/data/dev.log"

if [[ ! -f "$LOG_FILE" ]]; then
    echo "No log file found at $LOG_FILE"
    exit 1
fi

if [[ "${1:-}" == "-f" ]]; then
    tail -f "$LOG_FILE"
elif [[ -n "${1:-}" ]]; then
    tail -n "$1" "$LOG_FILE"
else
    tail -n 50 "$LOG_FILE"
fi
