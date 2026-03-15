# CLAUDE.md

## Project Overview

MediaSkipDetector is a GPL-3.0 licensed standalone service that detects skippable content
in media files (TV intros, end credits, advertisements). It scans directories on a NAS for
TV season structures, analyzes audio fingerprints across episodes to find repeated segments,
and writes detection results as `.skip.json` files alongside the source media files.

This project has **no runtime coupling** to any media management application. It communicates
exclusively through the filesystem — producing `.skip.json` files that consumers read
independently. This separation allows MediaSkipDetector to use GPL-licensed libraries
(Chromaprint, Jellyfin intro-skipper algorithms) without affecting the license of any consumer.

The history of the project's evolution is recorded in `claude.log` in the project root.

## License

**GPL-3.0-only.** This license is required for compatibility with:
- Jellyfin intro-skipper (GPL-3.0) — reference implementation for audio fingerprint comparison
- Chromaprint (MIT/LGPL-2.1) — audio fingerprinting library (compatible with GPL-3.0)
- FFmpeg (LGPL-2.1) — media processing (compatible with GPL-3.0)

## Relationship to MediaManager

MediaSkipDetector and [MediaManager](https://github.com/jeffbstewart/MediaManager) are
completely independent projects with different licenses. They share no code, no libraries,
and no runtime dependencies. The only coupling is the `.skip.json` file contract
(see [Issue #1](https://github.com/jeffbstewart/MediaSkipDetector/issues/1)).

- MediaSkipDetector **writes** `.skip.json` files alongside source MKVs
- MediaManager **reads** `.skip.json` files during its NAS scan and imports them
- Neither project calls, links, or depends on the other

## Key Dependencies

- **Jellyfin intro-skipper** (GPL-3.0, C#) — the most complete open-source intro detection
  implementation. Uses Chromaprint for audio fingerprinting with an inverted index + shift
  voting + XOR contiguous match comparison algorithm.
- **Chromaprint** (MIT/LGPL-2.1, C/C++) — audio fingerprinting library. Best bindings:
  Python (`pyacoustid`), C# (`AcoustID.NET`). Also available via `fpcalc` CLI from any language.
- **FFmpeg** (LGPL-2.1) — media processing. Used by Chromaprint internally and for `ffprobe`.

**Language: C#** (.NET). Chosen for direct compatibility with the Jellyfin intro-skipper
codebase (GPL-3.0, C#) — the primary reference implementation we're adapting from.
Build toolchain is `dotnet` CLI (no Gradle).

## Build and Run Commands

*(To be populated once project is scaffolded.)*

**Important:** All `dotnet` CLI invocations must set `DOTNET_CLI_TELEMETRY_OPTOUT=1` to
disable Microsoft telemetry. Lifecycle scripts handle this automatically. For manual
invocations, prefix with the environment variable:

```bash
DOTNET_CLI_TELEMETRY_OPTOUT=1 dotnet build
```

## Lifecycle Scripts

Scripts in `lifecycle/` manage development and deployment tasks:

**Important:** Never use broad process kills (`taskkill //F //IM java.exe` or
`taskkill //F //IM dotnet.exe`) without explicit permission. Other processes
(e.g., the MediaManager transcode buddy) may be running on the same machine.
Use targeted scripts instead:

```bash
./lifecycle/run-dev.sh          # Start dev server (output to data/dev.log)
./lifecycle/stop-dev.sh         # Stop dev server
./lifecycle/dev-log.sh          # Last 50 lines of dev log
./lifecycle/dev-log.sh -f       # Follow dev log
./lifecycle/dev-log.sh 100      # Last 100 lines
```

## Architecture

### Filesystem Contract

Skip detection results are written as JSON files alongside source media:

```
{video_basename}.{agentname}.skip.json
```

Each file contains a JSON array of skip segments:

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

Required fields: `start` (seconds), `end` (seconds), `region_type` (string).
Additional fields are permitted and encouraged.

**Region types:** `INTRO`, `END_CREDITS`, `ADS` (extensible).

### Directory Scanning

The scanner looks for TV season structures — directories containing files with
`S\d+E\d+` patterns in the filename. It works from `.mkv` source files, since skip
timestamps are intrinsic to the content and shared across all transcodes.

### Key Directories

- `secrets/` — Environment files (gitignored, never commit)
- `lifecycle/` — Build, deploy, and maintenance scripts
- `data/` — Runtime data, logs (gitignored)
- `docs/` — Documentation

## Security

- Claude must **never read files with the `.env` extension** in `secrets/` (contains real API keys and passwords)
- Claude **may read `.agent_visible_env` files** in `secrets/` (test credentials, non-secret config)
- Values from `secrets/.env` must **never be committed to source control or logged**
- Use `secrets/example.env` as the template for documenting required environment variables

## Philosophy

**"Debugging sucks, Testing Rocks."** — The comparison algorithms are pure functions with
no I/O dependencies. Test them thoroughly with synthetic and real fingerprint data.

## Version Control

This repository uses **git** hosted on GitHub. See `.gitignore` for excluded files.

### Commit Message Style

```
Short summary line (imperative mood, ~50 chars)

Body paragraph(s) describing what changed and why. Wrap at ~72 chars.
Use bullet points with `-` for lists of changes.

Co-Authored-By: Claude Opus 4.6 <noreply@anthropic.com>
```

### Presubmit Check

A git pre-commit hook scans staged changes for sensitive data (hardcoded IPs, UUIDs,
API keys, etc.). If the commit is rejected, fix the violations or add known-safe values
to `lifecycle/presubmit-allowlist.txt`.

## Docker Deployment

*(To be added once Docker mechanics are implemented.)*

## Conversation Transcript

All Claude Code conversations for this project must be logged to `claude.log` in the
project root. Log **every substantive exchange** — not just at session end, but as the
conversation progresses. Each entry must include a date and timestamp. Format:

```
=== YYYY-MM-DD HH:MM — <brief topic> ===
- What was discussed
- What was decided
- What was changed (files created/modified/deleted)
===
```

This log serves as a persistent project history across sessions. Always append; never overwrite.
