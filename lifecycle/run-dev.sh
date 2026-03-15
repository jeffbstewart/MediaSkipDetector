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

# Source environment variables
ENV_FILE="$PROJECT_ROOT/secrets/.env"
if [[ -f "$ENV_FILE" ]]; then
    set -a
    source "$ENV_FILE"
    set +a
else
    echo "Warning: $ENV_FILE not found. Copy from secrets/example.env and fill in values."
fi

if [[ -z "${MEDIA_ROOT:-}" ]]; then
    echo "Error: MEDIA_ROOT must be set in secrets/.env or environment"
    exit 1
fi

echo "Building..."
cd "$SRC_DIR"
dotnet build --nologo 2>&1 || { echo "Build failed"; exit 1; }

# Run the native executable directly (not via 'dotnet run' or 'dotnet *.dll')
# so the PID is the actual app process and shutdown signals reach it.
EXE_PATH="$SRC_DIR/bin/Debug/net10.0/MediaSkipDetector.exe"
if [[ ! -f "$EXE_PATH" ]]; then
    echo "Build output not found: $EXE_PATH"
    exit 1
fi

# Report DB state before launch (DATA_DIR may be set from .env above)
DB_PATH="${DATA_DIR:-./data}/skipdetector.db"
if [[ -f "$DB_PATH" ]]; then
    DB_SIZE=$(du -h "$DB_PATH" | cut -f1)
    echo "Database exists: $DB_PATH ($DB_SIZE)"
else
    echo "Database missing (will be created): $DB_PATH"
fi

echo "Starting MediaSkipDetector (logs: $LOG_FILE)..."
"$EXE_PATH" > "$LOG_FILE" 2>&1 &
echo $! > "$PID_FILE"
echo "MediaSkipDetector started (PID $(cat "$PID_FILE"))."
