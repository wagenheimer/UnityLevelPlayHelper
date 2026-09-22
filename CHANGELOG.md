# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [2.0.0] - 2026-09-22

### Changed (breaking)
- Minimum Unity version is now **2022.3 LTS** (was 2021.3): the debug overlay is built with runtime UI Toolkit and uses `IStyle.translate`.
- `LevelPlayDebugOverlay` was rewritten from IMGUI to runtime UI Toolkit (`UIDocument` + `PanelSettings` created in code). It no longer uses `OnGUI`, so nothing is clipped, the log scrolls/filters and diagnostics can be copied to the clipboard. Toggle key, floating button and `CreateOverlay()` are unchanged.

### Fixed
- **Ads could silently never load**: ad objects were only created inside `LevelPlay.OnInitSuccess`, which does not fire in every SDK configuration in the Editor. A watchdog now creates them anyway after 6s in the Editor (mock ads work without the callback) and reports the stall instead of leaving an empty log.
- **Duplicate SDK event subscriptions**: a failed init retried via `Invoke(Initialize)` and re-subscribed the static `OnInitSuccess`/`OnInitFailed` handlers every attempt, so callbacks (and ad object creation) fired N times. Subscriptions are now remove-then-add and ad objects are created once.
- **The overlay contradicted the runtime in the Editor**: it reported the raw Inspector fields while the helper actually used Editor mock credentials, so a working mock setup showed "NO APP KEY" / "NOT CONFIGURED" and the Show buttons were disabled. Diagnostics are now mock-aware (`EffectiveHas*`, `UsesMock*`).
- Banner ads now work in Play mode too: an Editor mock banner Ad Unit ID is used when the Inspector banner ID is empty.

### Added
- Central diagnostic log on `LevelPlayHelper` (500 entries, thread-safe, mirrored to the console) with `AdLogLevel`, `SnapshotLog()`, `ClearLog()` and the `OnDiagnosticLog` event.
- `SdkInitState` / `AdFormatState` state machines and `AdFormatDiagnostics` per format: loading duration, last error code + message, retry attempt, next-retry countdown, last network/placement/revenue.
- `LevelPlayHelper.Diagnose()` — a one-line "why are ads not loading?" explanation, surfaced as a banner at the top of the overlay.
- `LevelPlayHelper.BuildDiagnosticReport()` and `CopyDiagnosticReportToClipboard()` for bug reports; the overlay also has Copy Log / filter / per-level colors.
- Overlay: safe-area aware, screen-clamped, maximizable, runtime scale (A- / A+), no emoji glyphs (they rendered as tofu in the default GUI font).
## [1.9.1] - 2026-09-21

### Changed
- chore(deps): bump PackageHub bootstrap to v1.0.5

## [1.9.0] - 2026-09-21

## [1.8.0] - 2026-09-20

### Added
- In-game `LevelPlayDebugOverlay` (Runtime/UI) that auto-attaches in the Editor and Development Builds when `enableDebugOverlay` is on: SDK/consent state, per-format readiness with retries, manual triggers (init, force reload, show each format, test suite, consent) and a live ad lifecycle event log. Toggle with F8 or the "ADS DBG" button.
- Ad lifecycle events on `LevelPlayHelper`: `OnSdkInitializeFailed`, `OnAdLoaded`, `OnAdLoadFailed`, `OnAdDisplayed`, `OnAdDisplayFailed`.
- Read-only diagnostics for the overlay: loading flags, retry counters, per-platform configuration and resolved IDs.
- `ForceReloadAds()` and `LaunchTestSuite()` public helpers.
- Setup checklist: "Fix all" (runs every safe one-click fix at once) and "Copy report" (whole checklist as Markdown), plus an in-game debug overlay check with a one-click enable.

### Fixed
- Setup checklist: the Android native dependencies check no longer reports the same status for both branches (dead ternary); it now distinguishes a partial from a missing resolve.

## [1.7.0] - 2026-09-19

## [1.6.0] - 2026-09-19

### Added
- Auto-installs `com.wagenheimer.packagehub` via git if missing, using a zero-dependency Editor bootstrap assembly (`PackageHubBootstrap`). Installing this package now pulls in PackageHub automatically, with no manual manifest edits or scoped registry required.

### Fixed
- Removed `com.wagenheimer.packagehub` from `dependencies` in package.json: UPM does not support a git URL as a dependency version, which made this package fail to resolve/update (or fail to compile once PackageHub was needed but never installed) in any consuming project. The git-based auto-bootstrap replaces this declaration.

## [1.5.0] - 2026-09-18

## [1.4.2] - 2026-09-18

### Changed
- **Centralized Update Management**: Replaced standalone update checker with dependency on `com.wagenheimer.packagehub` (`UnityPackageHub`). Updates, changelogs, and package management are now handled centrally through the unified Wagenheimer Package Hub.

## [1.4.1] - 2026-09-17

### Fixed
- stop counting method declarations as ad format usage

## [1.4.0] - 2026-09-17

### Added
- add one-click fixes and AI prompts to the setup checklist

## [1.3.2] - 2026-09-17

### Fixed
- grant the Editor mock reward when the ad closes without a callback

## [1.3.1] - 2026-09-17

### Fixed
- add the missing meta file for the editor test mode script

## [1.3.0] - 2026-09-17

### Added
- test ads and IAP in Play mode without touching Player Settings

## [1.2.1] - 2026-09-17

### Fixed
- stop chaining Facts.Add so the setup checklist compiles

## [1.2.0] - 2026-09-17

### Added
- rebuild the setup checklist on UI Toolkit with full LevelPlay coverage

## [1.1.1] - 2026-08-22

### Fixed
- add missing .meta files, fix impression AdUnitId, add setup checklist and docs

## [1.1.0] - 2026-08-22

### Added
- initial release of Level Play Helper - LevelPlay ads manager with init, consent, interstitial/rewarded/banner lifecycle, retries and ILRD events

