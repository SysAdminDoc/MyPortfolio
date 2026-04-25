# Roadmap

MyPortfolio uses this file as the public implementation checklist. Local scratch notes stay in ignored files.

## Completed

- [x] v0.2.0 — Logo and branding
  - Added the Catppuccin three-tab brand mark as SVG, PNG, size variants, multi-resolution Windows ICO, README banner, README logo, and in-app header artwork.
  - Wired the WPF application and main window to the branded icon.

## Next

- [ ] v0.3.0 — Refresh on launch
  - Add a user setting to refresh all tabs after startup.
  - Track and show last-refresh timestamps per tab.

- [ ] v0.4.0 — Multi-owner settings UI
  - Replace the comma-separated extra owners textbox with a chip/list editor.
  - Keep existing comma/semicolon/newline parsing as an import path for compatibility.

- [ ] v0.5.0 — Theme expansion
  - Add Catppuccin Latte light theme.
  - Add a small accent picker that reuses the existing token system.

- [ ] v0.6.0 — APK metadata enrichment
  - Read `AndroidManifest.xml` from APK ZIPs where feasible.
  - Surface package name, version code, and version name in Android cards.
