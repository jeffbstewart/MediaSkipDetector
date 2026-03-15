#!/usr/bin/env bash
# run-dev.sh — Start the MediaSkipDetector in development mode.
# Output goes to data/dev.log to keep the terminal clean.
# Use lifecycle/stop-dev.sh to stop.

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
PROJECT_ROOT="$(cd "$SCRIPT_DIR/.." && pwd)"
SRC_DIR="$PROJECT_ROOT/src"
LOG_DIR="$PROJECT_ROOT/data"
LOG_FILE="$LOG_DIR/dev.log"
PID_FILE="$LOG_DIR/dev.pid"

mkdir -p "$LOG_DIR"

# Check if already running
if [[ -f "$PID_FILE" ]]; then
    old_pid=$(cat "$PID_FILE")
    if kill -0 "$old_pid" 2>/dev/null; then
        echo "Already running (PID $old_pid). Use lifecycle/stop-dev.sh first."
        exit 1
    fi
    rm -f "$PID_FILE"
fi

export DOTNET_CLI_TELEMETRY_OPTOUT=1

echo "Building..."
cd "$SRC_DIR"
dotnet build --nologo -q 2>&1 || { echo "Build failed"; exit 1; }

# Run the native executable directly (not via 'dotnet run' or 'dotnet *.dll')
# so the PID is the actual app process and shutdown signals reach it.
EXE_PATH="$SRC_DIR/bin/Debug/net10.0/MediaSkipDetector.exe"
if [[ ! -f "$EXE_PATH" ]]; then
    echo "Build output not found: $EXE_PATH"
    exit 1
fi

echo "Starting MediaSkipDetector (logs: $LOG_FILE)..."
"$EXE_PATH" > "$LOG_FILE" 2>&1 &
echo $! > "$PID_FILE"
echo "MediaSkipDetector started (PID $(cat "$PID_FILE"))."
