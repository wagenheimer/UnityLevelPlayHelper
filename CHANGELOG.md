# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

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

