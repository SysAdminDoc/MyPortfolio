# Roadmap

MyPortfolio uses this file as the public implementation checklist. Local scratch notes stay in ignored files.

## Completed

- [x] v0.2.0 — Logo and branding
  - Added the Catppuccin three-tab brand mark as SVG, PNG, size variants, multi-resolution Windows ICO, README banner, README logo, and in-app header artwork.
  - Wired the WPF application and main window to the branded icon.

- [x] v0.3.0 — Refresh on launch
  - Add a user setting to refresh all tabs after startup.
  - Track and show last-refresh timestamps per tab.
  - Added a settings drawer toggle, startup refresh hook, persisted UTC timestamps, and compact per-tab freshness copy.

- [x] v0.4.0 — Multi-owner settings UI
  - Replace the comma-separated extra owners textbox with a chip/list editor.
  - Keep existing comma/semicolon/newline parsing as an import path for compatibility.
  - Added add/remove/clear chip actions, Enter-to-add handling, duplicate filtering, primary-owner filtering, and pasted-list import.

- [x] v0.5.0 — Theme expansion
  - Add Catppuccin Latte light theme.
  - Add a small accent picker that reuses the existing token system.
  - Added runtime Mocha / Latte token switching plus Mauve, Sapphire, Teal, Green, Peach, and Red accent choices.

- [x] v0.6.0 — APK metadata enrichment
  - Read `AndroidManifest.xml` from APK ZIPs where feasible.
  - Surface package name, version code, and version name in Android cards.
  - Added binary/plain manifest decoding for downloaded APKs, persisted metadata in the Android downloads manifest, and included metadata in card display and search.

- [x] v0.7.0 — Local artifact details
  - Add a consistent details surface for installed/downloaded artifacts across tabs.
  - Include file path, SHA-256, asset size, release date, and copy/open actions without crowding cards.
  - Added collapsible card details for Desktop, Chrome, and Android plus persisted source asset metadata for newly installed/downloaded artifacts.

- [x] v0.8.0 — Catalog resilience and diagnostics
  - Surface GitHub rate-limit and partial-failure state without burying it in the activity log.
  - Add clearer per-owner discovery summaries when one configured owner fails and others still load.
  - Added visible per-tab discovery diagnostics with owner summaries, partial-failure warnings, probe issue counts, and current GitHub API quota.

## Next

- [ ] v0.9.0 — Discovery performance controls
  - Add a per-tab owner/repo concurrency cap or staged refresh mode.
  - Cache release probes briefly to avoid repeated refreshes burning GitHub API quota.
