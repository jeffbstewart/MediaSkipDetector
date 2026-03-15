#!/usr/bin/env bash
# kill-gradle.sh — Kill Gradle daemons only (not other Java processes).
#
# IMPORTANT: Never use broad Java process kills (taskkill //F //IM java.exe)
# without explicit permission. Other Java processes (e.g., the MediaManager
# transcode buddy) may be running on the same machine.

set -euo pipefail

if command -v jps &>/dev/null; then
    # Use jps to find Gradle daemons specifically
    pids=$(jps -l 2>/dev/null | grep -i "GradleDaemon\|gradle" | awk '{print $1}' || true)
    if [[ -n "$pids" ]]; then
        for pid in $pids; do
            echo "Killing Gradle daemon (PID $pid)"
            kill "$pid" 2>/dev/null || true
        done
    else
        echo "No Gradle daemons found"
    fi
else
    # Fallback: use Gradle's own stop command
    if [[ -f ./gradlew ]]; then
        ./gradlew --stop 2>/dev/null || true
        echo "Sent Gradle daemon stop request"
    else
        echo "No gradlew found and jps not available"
    fi
fi
