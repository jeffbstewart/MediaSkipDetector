# Vendoring and Rebasing intro-skipper

## Overview

MediaSkipDetector vendors the [intro-skipper](https://github.com/intro-skipper/intro-skipper)
Jellyfin plugin to reuse its Chromaprint fingerprint comparison algorithm. The full upstream
repository lives under `vendor/intro-skipper/`, managed as a **git subtree**. Only a small
subset of files are compiled into our build.

## Why git subtree?

- **Rebasing**: `git subtree pull` merges upstream changes, handling conflicts with standard
  git tooling. Local modifications survive the merge.
- **No submodule ceremony**: Clones, CI, and Docker builds work without `--recurse-submodules`.
- **Full upstream context**: Keeping the entire repo (not just extracted files) means subtree
  pull can track renames, moves, and refactors upstream.
- **Unused files cost nothing**: They sit in `vendor/` but are never compiled.

## How it was created

```bash
# Initial vendor from the 10.12 branch (2026-03-15)
git subtree add --prefix=vendor/intro-skipper \
    https://github.com/intro-skipper/intro-skipper.git 10.12 --squash
```

The `--squash` flag collapses upstream history into a single merge commit, keeping our
log clean while preserving the ability to pull future updates.

Note: intro-skipper uses version branches (`10.10`, `10.11`, `10.12`) instead of `main`.

## Which files are compiled

The `.csproj` selectively includes only the files we need via `<Compile Include="..."/>`
entries with `Link="Vendor\..."` attributes so they appear under a virtual `Vendor/` folder
in IDEs. The full upstream repo is present on disk but not compiled.

**Currently included files from `vendor/intro-skipper/IntroSkipper/`:**

| File | Purpose |
|------|---------|
| `Analyzers/ChromaprintAnalyzer.cs` | Core algorithm: inverted index, shift voting, XOR matching |
| `Analyzers/TimeAdjustmentHelper.cs` | Post-analysis intro time refinement (chapter snapping, silence, keyframes) |
| `Configuration/PluginConfiguration.cs` | Algorithm tuning parameters (thresholds, limits) |
| `Data/AnalysisMode.cs` | Enum: Introduction, Credits, Preview, Recap, Commercial |
| `Data/EpisodeState.cs` | Enum: NotAnalyzed, Analyzed, NoSegments |
| `Data/FingerprintException.cs` | Exception type for fingerprinting errors |
| `Data/PluginWarning.cs` | Warning flags enum |
| `Data/QueuedEpisode.cs` | Episode metadata for analysis queue |
| `Data/QueuedMediaCategory.cs` | Enum: Episode, AnimeEpisode, Movie |
| `Data/Segment.cs` | Result type: episode ID + start/end times |
| `Data/TimeRange.cs` | Time range data type with duration and comparison |
| `Data/TimeRangeHelpers.cs` | Finds longest contiguous time range from timestamps |
| `Data/WarningManager.cs` | Static warning flag accumulator |

**Not included** (Jellyfin-coupled code we don't need):

- `Analyzers/BlackFrame*.cs`, `ChapterAnalyzer.cs` — alternative analyzers using FFmpeg filters
- `Analyzers/IMediaFileAnalyzer.cs` — Jellyfin plugin interface (shimmed in `VendorShims.cs`)
- `Controllers/`, `Services/`, `Manager/`, `Db/` — Jellyfin plugin infrastructure
- `FFmpegWrapper.cs` — Jellyfin's FFmpeg integration (shimmed; we use our own `FpcalcService`)
- `Helper/`, `Filters/`, `Providers/`, `ScheduledTasks/` — Jellyfin internals
- `Plugin.cs` — Jellyfin plugin singleton (shimmed in `VendorShims.cs`)

## Shim strategy

The vendored files reference Jellyfin SDK types that don't exist in our project. Rather
than modifying the vendored files (which would create merge conflicts on every rebase),
we provide shims in `src/VendorShims.cs` that satisfy the compiler:

| Shim | Why |
|------|-----|
| `MediaBrowser.Model.Plugins.BasePluginConfiguration` | Empty base class for `PluginConfiguration` |
| `MediaBrowser.Model.Entities.ChapterInfo` | Stub for `TimeAdjustmentHelper` chapter lookups |
| `IntroSkipper.Analyzers.IMediaFileAnalyzer` | Interface that `ChromaprintAnalyzer` declares it implements |
| `IntroSkipper.Plugin` | Singleton providing `Configuration` and no-op `UpdateTimestampAsync` |
| `IntroSkipper.FFmpegWrapper` | Static methods that throw `NotSupportedException` (never called) |

**Key principle**: We don't call `AnalyzeMediaFiles` (the Jellyfin orchestration method).
We call `CompareEpisodes` directly with pre-computed `uint[]` fingerprint arrays. The shims
exist only to make the vendored code compile — they are not invoked at runtime except for
`Plugin.Instance.Configuration`, which returns our algorithm parameters.

### Initializing the Plugin shim

Before using `ChromaprintAnalyzer`, set up the singleton:

```csharp
IntroSkipper.Plugin.Instance = new IntroSkipper.Plugin
{
    Configuration = new PluginConfiguration
    {
        MaximumFingerprintPointDifferences = 6,
        MaximumTimeSkip = 3.5,
        InvertedIndexShift = 2,
        MinimumIntroDuration = 15,
        MaximumIntroDuration = 120,
    }
};
```

## How to rebase (pull upstream changes)

```bash
# From the project root:
git subtree pull --prefix=vendor/intro-skipper \
    https://github.com/intro-skipper/intro-skipper.git 10.12 --squash

# If upstream moves to a new branch (e.g., 10.13):
git subtree pull --prefix=vendor/intro-skipper \
    https://github.com/intro-skipper/intro-skipper.git 10.13 --squash
```

### After pulling

1. **Resolve conflicts**: If you've modified any vendored files, git will show merge
   conflicts in those files. Resolve them as you would any merge conflict.

2. **Build immediately**: `cd src && dotnet build` — compiler errors will surface any
   breaking changes. Common issues:
   - New Jellyfin types referenced → add shims to `VendorShims.cs`
   - Changed method signatures → update our calling code
   - Moved/renamed files → update `<Compile Include="..."/>` in `.csproj`

3. **Check for new dependencies**: If upstream added new files that the included files
   now reference, either add them to the `<Compile>` list or add shims.

4. **Update this doc**: If the file list, branch, or shim list changed.

## Key algorithm parameters

These values from `PluginConfiguration` control the Chromaprint comparison. The defaults
work well for TV intro detection:

| Parameter | Default | Purpose |
|-----------|---------|---------|
| `MaximumFingerprintPointDifferences` | 6 | Max differing bits (of 32) between two points |
| `MaximumTimeSkip` | 3.5s | Max gap between similar points before breaking contiguity |
| `InvertedIndexShift` | 2 | Fuzzy matching tolerance for index lookup |
| `MinimumIntroDuration` | 15s | Shortest valid intro |
| `MaximumIntroDuration` | 120s | Longest valid intro |

## License

intro-skipper is GPL-3.0. MediaSkipDetector is also GPL-3.0, which is why this vendoring
is license-compatible. The vendored code retains its original SPDX headers.
