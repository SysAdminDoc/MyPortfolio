# Changelog

All notable changes to MyPortfolio are documented here. Format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and the project adheres to [Semantic Versioning](https://semver.org/).

## Unreleased

### Added
- Refresh-on-launch setting that runs the shared discovery pass after startup when enabled.
- Per-tab last-refresh timestamps for Desktop, Chrome, and Android discovery surfaces.
- Extra-owner chip editor with add, remove, clear, Enter-to-add, and pasted-list import support.
- Catppuccin Latte theme and accent picker backed by shared runtime theme tokens.
- APK manifest metadata reader that stores and displays package name, version name, and version code for downloaded Android builds.
- Confirmation prompt before removing a locally downloaded APK.
- Collapsible local artifact details on Desktop, Chrome, and Android cards with file path, release asset, SHA-256, release date, copy, and open actions.
- Per-tab discovery diagnostics that show owner scan summaries, partial-failure warnings, repo probe issues, and GitHub API quota after refresh.
- Short-lived, token-aware discovery probe cache plus larger GitHub repo-list pages to reduce repeated refresh quota usage.
- Staged refresh-all flow with cancellation and clearer GitHub primary/secondary rate-limit retry guidance.
- Per-tab refresh progress text and counts with visible skipped archived, hidden, and topic-filtered repository summaries.
- Expandable per-owner discovery diagnostics with matched, skipped, cached, failed, and probe-issue counts.
- One-click copied diagnostics bundles with owner summaries, rate-limit state, recent activity, and GitHub token redaction.
- Per-tab saved diagnostics bundles that write the same redacted support text to timestamped files and reveal them in Explorer.
- Tracked public roadmap file for completed and upcoming implementation passes.

## v0.1.0 — 2026-04-25

Initial release. Unifies LocalDesktopStore, LocalChromeStore, and LocalAndroidStore into a single Windows desktop catalog backed by GitHub releases.

### Added
- WPF / .NET 9 shell with TabControl: **Desktop apps**, **Chrome extensions**, **Android APKs**
- Catppuccin Mocha dark theme — single resource dictionary, shared across every tab
- Shared header (logo, refresh-all, settings toggle), shared settings drawer, shared activity log, shared status bar
- One `AppSettings` JSON file at `%APPDATA%\MyPortfolio\settings.json` driving every tab
- `GitHubClientFactory` rebuilt on token change so the PAT can be updated without restart
- `HttpDownloader` with stream-to-file (Desktop/Android) and bytes-to-memory (Chrome) variants
- `LogSink` thread-safe ring buffer (600 lines), every tab streams into it with a `[Tab]` prefix
- `HashVerifier` shared SHA-256 sidecar verification used by Desktop + Android tabs

#### Desktop apps tab (ported from LocalDesktopStore)
- Discovery via Octokit — every repo whose latest release ships an MSI / EXE / ZIP asset
- Asset classifier — name hints first, then bounded byte scan + `FileVersionInfo` to distinguish Inno / NSIS / generic
- Install handlers: MSI (`msiexec /i ... /qb /norestart` with verbose log), Inno (`/SILENT /NORESTART`), NSIS (`/S`), generic interactive, portable ZIP (extract + Start Menu shortcut)
- Uninstall handlers: MSI (`/x ProductCode`), Inno/NSIS via recorded `UninstallString`, portable folder + shortcut delete
- Run handler: registry `DisplayIcon` → `InstallLocation` primary `.exe` → portable launcher
- Registry-diff install detection across `HKLM\Uninstall`, `HKLM\WOW6432Node\Uninstall`, `HKCU\Uninstall`
- SHA-256 sidecar verification toggle
- Per-tab topic filter (default `windows-app`)
- Install root override (default `%LOCALAPPDATA%\MyPortfolio\desktop\apps`)

#### Chrome extensions tab (ported from LocalChromeStore)
- Discovery — repos with manifest.json at common paths or release ZIP/CRX
- Manifest enrichment — name / version / description / largest icon read from the ZIP entry or repo content API
- Zip-slip guard, single-wrapper-folder flatten
- CRX2 (pubkeyLen + sigLen) + CRX3 (headerLen) header strip → inner ZIP extract
- Browser launcher — Chrome / Brave / Edge / Vivaldi / Opera / Chromium auto-detect
- `--load-extension="path1,path2,..."` comma-separated (Chromium ≥75 requirement)
- Hide repo command — per-repo exclusion stored in `HiddenRepos`, restorable from settings drawer
- Per-tab topic filter (default `chrome-extension`)

#### Android APKs tab (download-only, new)
- Discovery — every repo whose latest release contains a `.apk` asset
- Picks the largest `.apk` (defaults to the signed release build over a debug variant)
- Downloads to `%USERPROFILE%\Downloads\MyPortfolio\Android\<owner>\<repo>\<version>\<asset>.apk`
- Optional SHA-256 sidecar verification (failure logs but doesn't delete — APK is already on disk)
- Computes a local SHA-256 hash even when no sidecar exists
- "Reveal in Explorer" highlights the file inside its containing folder
- "Remove" deletes the per-repo download folder and clears the manifest row
- Update-detection card state + "Re-download" / "Update to vX.Y.Z" labels matching the other tabs
- Download root override (default `%USERPROFILE%\Downloads\MyPortfolio\Android`)
- Per-tab topic filter (default `android-app`)

### Notes
- The Windows host has no native APK install path. The Android tab intentionally stops at download — sideloading is left to the user (`adb install`, file transfer, etc.). The Android-side install pipeline (signature pinning, PackageInstaller.Session) lives in the original LocalAndroidStore Android app.
- Crash log writer at `%LOCALAPPDATA%\MyPortfolio\logs\` covers every tab.
