#!/usr/bin/env bash
# stop-dev.sh — Stop the MediaSkipDetector dev process.

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
PROJECT_ROOT="$(cd "$SCRIPT_DIR/.." && pwd)"
PID_FILE="$PROJECT_ROOT/data/dev.pid"

if [[ ! -f "$PID_FILE" ]]; then
    echo "No PID file found (not running?)"
    exit 0
fi

pid=$(cat "$PID_FILE")
if kill -0 "$pid" 2>/dev/null; then
    echo "Stopping MediaSkipDetector (PID $pid)..."

    # Prefer /quitquitquit for graceful shutdown (works reliably on all platforms).
    # Fall back to OS-level signals if the HTTP endpoint isn't responding.
    port="${SKIPDETECT_PORT:-16004}"
    if curl -sf -X POST "http://localhost:$port/quitquitquit" > /dev/null 2>&1; then
        echo "Graceful shutdown requested via /quitquitquit"
    elif command -v taskkill &>/dev/null; then
        taskkill //PID "$pid" > /dev/null 2>&1 || true
    else
        kill -INT "$pid" 2>/dev/null || true
    fi

    # Wait up to 5 seconds for graceful shutdown
    for i in {1..10}; do
        if ! kill -0 "$pid" 2>/dev/null; then
            break
        fi
        sleep 0.5
    done
    if kill -0 "$pid" 2>/dev/null; then
        echo "Force killing..."
        if command -v taskkill &>/dev/null; then
            taskkill //F //PID "$pid" > /dev/null 2>&1 || true
        else
            kill -9 "$pid" 2>/dev/null || true
        fi
    fi
    echo "Stopped."
else
    echo "Process $pid is not running."
fi
rm -f "$PID_FILE"
