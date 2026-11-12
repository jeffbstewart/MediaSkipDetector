# Analysis Tuning Parameters

All parameters have sensible defaults inherited from the Jellyfin intro-skipper
project. You only need to set these if the defaults aren't producing good results
for your library.

Set via environment variables (in `secrets/.env` for local dev, or in
`docker-compose.yml` for production).

## Fingerprinting

| Variable | Default | Description |
|----------|---------|-------------|
| `FPCALC_PATH` | _(auto-detect)_ | Path to fpcalc binary. Auto-detected on PATH if not set. In Docker, installed via `apk add chromaprint`. |
| `FFMPEG_PATH` | _(auto-detect)_ | Path to ffmpeg binary. Needed for credits detection (tail audio extraction). Auto-detected on PATH if not set. In Docker, installed via `apk add ffmpeg`. ffprobe is located automatically as a sibling of ffmpeg. |
| `FINGERPRINT_LENGTH_SECONDS` | `600` | Seconds of audio to fingerprint per episode (default 10 minutes, matching Jellyfin's `AnalysisLengthLimit`). Each fingerprint point covers ~0.12s, so 600s produces ~4800 points. Reducing to 120 saves CPU/cache but misses intros that start after 2 minutes. |

## Comparison Strategy

| Variable | Default | Description |
|----------|---------|-------------|
| `MAX_COMPARISON_CANDIDATES` | `7` | Max other episodes each episode is compared against. Candidates are spread across the season via hashing rather than just nearest neighbors, giving good coverage. Higher values find more intros at the cost of more CPU time. |

For a 20-episode season with the default of 7: at most 140 comparisons worst
case, typically far fewer due to early exit on first match and skipping episodes
that already matched.

## Chromaprint Matching Algorithm

These control the vendored intro-skipper `ChromaprintAnalyzer`. The defaults are
well-tested by the Jellyfin community across thousands of libraries.

| Variable | Default | Description |
|----------|---------|-------------|
| `MAX_FINGERPRINT_POINT_DIFFERENCES` | `6` | Max number of differing bits (out of 32) between two Chromaprint points for them to be considered a match. Lower = stricter matching, fewer false positives but may miss intros with slight audio variations. Higher = more permissive. Range: 0-32. |
| `MAX_TIME_SKIP` | `3.5` | Max gap in seconds between two matching fingerprint points before breaking the contiguous range. Handles brief non-matching segments within an otherwise matching intro (e.g., network bumper variations). |
| `INVERTED_INDEX_SHIFT` | `2` | Fuzzy matching tolerance when searching the inverted index. For each fingerprint point, also checks `point ± shift` values. Higher = more candidate shifts explored, better recall but slower. |
| `MIN_INTRO_DURATION` | `15` | Minimum length (seconds) for a detected segment to be considered a valid intro. Shorter matches are discarded as noise. |
| `MAX_INTRO_DURATION` | `120` | Maximum length (seconds) for a detected segment. Longer matches are discarded (likely a false positive from ambient audio). |

## Credits Detection

Credits detection uses the same Chromaprint algorithm but applied to the tail end
of each episode. It works by extracting the last N seconds of audio via ffmpeg,
fingerprinting it, reversing the fingerprint array, and running the same pairwise
comparison. Detected timestamps are then converted back to absolute positions.

Requires ffmpeg (for tail audio extraction) and ffprobe (for duration detection).
If ffmpeg is not available, credits detection is silently skipped.

| Variable | Default | Description |
|----------|---------|-------------|
| `CREDITS_FINGERPRINT_SECONDS` | `300` | Seconds of tail audio to fingerprint per episode (default 5 minutes). For most TV episodes (22-44 min), 300s covers the entire credits sequence plus some preceding content. Increase for very long episodes. |
| `MIN_CREDITS_DURATION` | `15` | Minimum length (seconds) for a detected segment to be considered valid credits. |
| `MAX_CREDITS_DURATION` | `300` | Maximum length (seconds) for a detected credits segment. Credits sequences are typically 30s-3min. |

The Chromaprint matching parameters (`MAX_FINGERPRINT_POINT_DIFFERENCES`,
`MAX_TIME_SKIP`, etc.) are shared between intro and credits detection.

## When to Adjust

**No intros detected (false negatives):**
- Increase `MAX_FINGERPRINT_POINT_DIFFERENCES` (try 8-10) — allows fuzzier matching
- Increase `MAX_TIME_SKIP` (try 5.0) — bridges longer gaps in the match
- Increase `MAX_COMPARISON_CANDIDATES` (try 10-12) — more chances to find a match
- Increase `FINGERPRINT_LENGTH_SECONDS` (try 180) — catches intros later in the episode

**Wrong segments detected (false positives):**
- Decrease `MAX_FINGERPRINT_POINT_DIFFERENCES` (try 4) — stricter matching
- Increase `MIN_INTRO_DURATION` (try 20-30) — discards short spurious matches
- Decrease `MAX_INTRO_DURATION` (try 90) — if your intros are shorter than 2 minutes
- Decrease `MAX_TIME_SKIP` (try 2.0) — tighter contiguity requirement

**Ambient audio matching (e.g., warp core hum):**
This is the failure mode that motivated using the intro-skipper algorithm over a
simpler approach. The inverted index + shift voting is specifically designed to
distinguish structured intro music from repetitive ambient audio. If it still
matches ambient sounds, decrease `MAX_FINGERPRINT_POINT_DIFFERENCES` and
`INVERTED_INDEX_SHIFT`.
