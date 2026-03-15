# MediaSkipDetector

A standalone service that detects skippable content in media files — TV intros, end credits, and advertisements. It scans directories for TV season structures, compares audio fingerprints across episodes to find repeated segments, and writes detection results as `.skip.json` files alongside the source media files.

MediaSkipDetector has no runtime coupling to any media management application. It communicates exclusively through the filesystem. Any media server that reads `.skip.json` files can consume the results.

## Quick Start (Docker)

### 1. Create a docker-compose.yml

```yaml
services:
  skipdetector:
    image: ghcr.io/jeffbstewart/mediaskipdetector:latest
    container_name: skipdetector
    restart: unless-stopped
    user: "1046:100"  # See "Container User ID" below
    ports:
      - "16004:16004"  # Monitoring: /health, /metrics, /status
    volumes:
      - /volume1/your-media-share:/media
    environment:
      - MEDIA_ROOT=/media
    healthcheck:
      test: ["CMD", "curl", "-f", "http://localhost:16004/health"]
      interval: 30s
      timeout: 5s
      start_period: 10s
      retries: 3
    stop_grace_period: 30s
```

### 2. Configure

| Setting | Required | Purpose |
|---------|----------|---------|
| `MEDIA_ROOT` | **Yes** | Path to media files inside the container (must match volume mount) |
| `user` | **Yes** | UID:GID with read/write access to your media files (see below) |

The media volume must be mounted **read/write** — the scanner writes `.skip.json` result files alongside the source media.

### 3. Launch

```bash
docker compose up -d
```

Or paste the docker-compose.yml into Portainer's stack editor and click **Deploy**.

### 4. Verify

```bash
curl http://your-host:16004/health
# {"status":"healthy"}

curl http://your-host:16004/status
# HTML status page showing uptime and health
```

---

## Container User ID

The `user:` directive in docker-compose.yml controls which UID:GID the container process runs as. This must match a user that has **read/write access** to your media files (read for scanning, write for `.skip.json` output).

### Finding Your UID

```bash
# SSH into your NAS or server
id -u your-username    # e.g., 1046
id -g your-username    # e.g., 100
```

Use these values in docker-compose.yml:
```yaml
user: "1046:100"
```

### Synology NAS: ACL Permissions

Synology DSM uses an **overlay ACL layer** on top of standard POSIX permissions. Even if a file shows `chmod 777`, access can be denied if the ACL doesn't grant the requesting UID access. Standard `ls -la` output can be misleading.

The UID must:

1. **Correspond to a real Synology user** — creating a Linux-only user inside the container won't work because Synology's ACL layer doesn't recognize it
2. **Be a member of the `users` group** (GID 100) — Synology's default shared folder permissions grant access to the `users` group
3. **Have explicit access to the shared folder** containing your media — in DSM, go to **Control Panel > Shared Folder > (your share) > Permissions** and ensure the user has Read/Write access

**Symptom:** Container starts and health check passes, but no `.skip.json` files are produced and logs show permission errors.

**Fix:** Use the UID of an existing Synology user who has access to the media shared folder.

---

## What It Produces

For each video file where skippable content is detected, the scanner writes a JSON file alongside the source:

```
Star Trek Voyager S01E03.mkv
Star Trek Voyager S01E03.introskip.skip.json
```

File naming: `{video_basename}.{agentname}.skip.json`

Each file contains a JSON array of detected segments:

```json
[
  {
    "start": 275.0,
    "end": 380.0,
    "region_type": "INTRO",
    "confidence": 0.95
  }
]
```

| Field | Type | Description |
|-------|------|-------------|
| `start` | number | Start time in seconds from beginning of video |
| `end` | number | End time in seconds from beginning of video |
| `region_type` | string | `INTRO`, `END_CREDITS`, or `ADS` (extensible) |

Additional fields (confidence, agent version, etc.) may be present and should be ignored by consumers that don't recognize them.

---

## Monitoring

| Endpoint | Method | Purpose |
|----------|--------|---------|
| `/health` | GET | Health check for Docker/orchestrators. Returns `{"status":"healthy"}` |
| `/metrics` | GET | Prometheus metrics in exposition format |
| `/status` | GET | HTML status page showing uptime and health |
| `/quitquitquit` | POST | Graceful shutdown |

Default port: **16004** (configurable via `--port=N`).

---

## Auto-Updates with Watchtower

[Watchtower](https://containrrr.dev/watchtower/) automatically pulls new images and restarts the container when updates are published to GHCR.

```yaml
services:
  watchtower:
    image: containrrr/watchtower
    container_name: watchtower
    restart: unless-stopped
    volumes:
      - /var/run/docker.sock:/var/run/docker.sock
    command: --interval 300 --cleanup skipdetector
```

---

## Development

### Prerequisites

- [.NET SDK 10.0+](https://dotnet.microsoft.com/download)
- **fpcalc** (Chromaprint CLI) — required for audio fingerprinting

#### Installing fpcalc on Windows

1. Download the latest Chromaprint release from https://github.com/acoustid/chromaprint/releases (look for `chromaprint-fpcalc-*-windows-x86_64.zip`)
2. Extract `fpcalc.exe` to a permanent location (e.g., `C:\fpcalc\fpcalc.exe`)
3. Set the path in your `secrets/.env` file:
   ```
   FPCALC_PATH=C:\fpcalc\fpcalc.exe
   ```
   Or pass it as a CLI argument: `--fpcalc-path=C:\fpcalc\fpcalc.exe`

Verify it works:
```bash
C:\fpcalc\fpcalc.exe -version
```

In Docker, `fpcalc` is installed automatically via the Alpine `chromaprint-tools` package.

### Build and Run

```bash
cd src
DOTNET_CLI_TELEMETRY_OPTOUT=1 dotnet build
```

### Lifecycle Scripts

```bash
./lifecycle/run-dev.sh          # Build and start (output to data/dev.log)
./lifecycle/stop-dev.sh         # Graceful stop via /quitquitquit
./lifecycle/dev-log.sh          # Last 50 lines of dev log
./lifecycle/dev-log.sh -f       # Follow dev log
```

---

## License

GPL-3.0. See [LICENSE](LICENSE) for details.

This license is required for compatibility with the [Jellyfin intro-skipper](https://github.com/intro-skipper/intro-skipper) (GPL-3.0) and [Chromaprint](https://github.com/acoustid/chromaprint) (MIT/LGPL-2.1) libraries used for audio fingerprinting.
