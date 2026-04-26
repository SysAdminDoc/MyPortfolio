<p align="center">
  <img src="banner.png" alt="MyPortfolio" width="720" />
</p>

<h1 align="center">
  <img src="logo.png" alt="" width="40" height="40" />
  MyPortfolio
</h1>

<p align="center">
  <a href="https://github.com/SysAdminDoc/MyPortfolio/releases"><img src="https://img.shields.io/badge/version-0.1.0-cba6f7?style=for-the-badge" alt="Version" /></a>
  <a href="LICENSE"><img src="https://img.shields.io/badge/license-MIT-a6e3a1?style=for-the-badge" alt="License" /></a>
  <a href="https://github.com/SysAdminDoc/MyPortfolio"><img src="https://img.shields.io/badge/platform-Windows%2010%2F11-74c7ec?style=for-the-badge" alt="Platform" /></a>
  <a href="https://dotnet.microsoft.com/"><img src="https://img.shields.io/badge/.NET-9.0-512BD4?style=for-the-badge" alt=".NET" /></a>
</p>

> **One desktop catalog for every app you ship.** MyPortfolio reads your GitHub releases and gives you a single Windows app to install desktop binaries, load Chromium extensions, and pull Android APKs across to your PC.

MyPortfolio replaces three separate stores — [LocalDesktopStore][lds], [LocalChromeStore][lcs], and [LocalAndroidStore][las] — with one shell. The Desktop and Chrome tabs install and uninstall like before. The Android tab is download-only on the Windows host: it pulls every `.apk` you publish straight into your local download folder so you can sideload it onto a device yourself.

[lds]: https://github.com/SysAdminDoc/LocalDesktopStore
[lcs]: https://github.com/SysAdminDoc/LocalChromeStore
[las]: https://github.com/SysAdminDoc/LocalAndroidStore

---

## What's in the app

| Tab | Source | What it does |
| --- | --- | --- |
| **Desktop apps** | `.msi`, `.exe` (Inno / NSIS / generic), `.zip` portable | Install, run, and uninstall — silent installers via `msiexec /qb`, `/SILENT /NORESTART`, `/S`, or extract-and-shortcut for portable ZIPs. SHA-256 sidecar verification. |
| **Chrome extensions** | `.zip` or `.crx` | Download, extract (zip-slip guarded, CRX2/CRX3 header strip), then launch Chrome / Brave / Edge / Vivaldi / Opera with `--load-extension="path1,path2,..."`. |
| **Android APKs** | `.apk` | Download to `%USERPROFILE%\Downloads\MyPortfolio\Android\<owner>\<repo>\<version>\` with hash verification. Read package name, version name, and version code from the APK manifest when available. Reveal in Explorer when you're ready to sideload. |

One settings drawer drives all three tabs — same GitHub user, same PAT, optional extra collaborator owners, shared Mocha / Latte appearance, accent color, separate topic filter / verification toggles per tab.

---

## Why one app instead of three

Three separate stores, three separate setups, three separate logs. Same GitHub user every time. The only thing that varied per store was *which release asset to pick and what to do with it*. So:

- **Shared shell** — one window, Catppuccin Mocha / Latte themes, one activity log feeding from every tab, one settings drawer.
- **Tabbed surface** — switch between Desktop / Chrome / Android with a click; per-tab refresh button + per-tab "Refresh all" trigger from the header.
- **One on-disk identity** — `%APPDATA%\MyPortfolio\settings.json`. Per-tab manifests for installed/downloaded state.

---

## Install

### From release (recommended)

1. Grab `MyPortfolio-vX.Y.Z-win-x64.zip` from the [Releases page](https://github.com/SysAdminDoc/MyPortfolio/releases).
2. Verify SHA-256 against the `.sha256.txt` sidecar.
3. Extract anywhere. Run `MyPortfolio.exe`.

Requires the [.NET 9 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/9.0) — Windows x64 Desktop Runtime installer.

### From source

```bash
git clone https://github.com/SysAdminDoc/MyPortfolio.git
cd MyPortfolio
dotnet build src/MyPortfolio/MyPortfolio.csproj -c Release
./src/MyPortfolio/bin/Release/net9.0-windows/MyPortfolio.exe
```

---

## Usage

1. Click **Settings** (top-right).
2. Set your **GitHub user / org** (defaults to `SysAdminDoc`).
3. *(Optional)* Paste a personal access token to raise the rate limit and surface private repos.
4. *(Optional)* Add **extra owners** — use the chip editor for collaborator users / orgs, or paste a comma / semicolon / newline-separated list.
5. Toggle topic filters per tab if you want to scope discovery (`windows-app`, `chrome-extension`, `android-app` are the suggested defaults).
6. Pick a Catppuccin **Theme** and **Accent** if you want a lighter surface or a different focus color.
7. Enable **Refresh all tabs when MyPortfolio starts** if you want discovery to run automatically on launch.
8. Click **Save and refresh all** — every tab populates simultaneously.
9. Switch between tabs and click **Install** / **Download APK** / **Launch with extensions** as you like.

Each tab shows its last successful refresh time beside its catalog summary, so stale discovery state is visible before you install or download anything. Downloaded Android cards also show the APK package name plus manifest version name and code when the manifest can be decoded.

Every action streams into the activity log at the bottom of the window. Nothing fails silently; everything is logged in-app and to `%LOCALAPPDATA%\MyPortfolio\logs\`.

---

## On-disk layout

```
%APPDATA%\MyPortfolio\
├── settings.json                 # one shared AppSettings instance
├── desktop-installed.json        # InstalledAppsManifest (Desktop tab)
├── chrome-installed.json         # InstalledExtensionsManifest (Chrome tab)
└── android-downloads.json        # DownloadedApksManifest (Android tab)

%LOCALAPPDATA%\MyPortfolio\
├── desktop\apps\<owner>\<repo>\<version>\    # portable ZIP extractions
├── desktop\downloads\                        # MSI / EXE staging
├── chrome\extensions\<owner>\<repo>\<version>\
├── cache\icons\                              # icon cache (per-tab prefix)
└── logs\                                     # crash logs

%USERPROFILE%\Downloads\MyPortfolio\Android\<owner>\<repo>\<version>\<asset>.apk
```

The Android download folder lives under your Downloads on purpose — it's where you actually look when you go to sideload.

---

## Architecture

C# WPF, .NET 9, single-project MVVM. No third-party MVVM toolkit. Three NuGet deps:

- `AndroidXml 1.1.24` — binary Android manifest decoding for APK metadata
- `Octokit 13.0.1` — GitHub API
- `Microsoft.Win32.Registry 5.0.0` — Windows uninstall key reads (Desktop tab only)

```
src/MyPortfolio/
├── App.xaml              # merged-dictionary theme bootstrap
├── MainWindow.xaml       # header / settings drawer / TabControl / log / status bar
├── MainViewModel.cs      # owns SettingsService, HttpDownloader, LogSink, three tab VMs
│
├── Common/               # ViewModelBase, RelayCommand, AppSettings, SettingsService,
│                         # GitHubClientFactory, HttpDownloader, HashVerifier, LogSink, Format
├── Converters/           # BoolToVis, NullToVis, EmptyStringToVis
├── Themes/               # Catppuccin token dictionary + runtime Mocha / Latte theme service
│
├── Desktop/              # Models / Services / ViewModels / Views — desktop-app install
├── Chrome/               # Models / Services / ViewModels / Views — extension install + launcher
└── Android/              # Models / Services / ViewModels / Views — APK download-only
```

Each tab owns its discovery service, its cards collection, and its install/download manifest. The shell exposes them through a single `MainViewModel`.

---

## Source repos that fed into this

| Repo | Status |
| --- | --- |
| [LocalDesktopStore][lds] | Code ported; functionality lives in the Desktop tab. |
| [LocalChromeStore][lcs] | Code ported; functionality lives in the Chrome tab. |
| [LocalAndroidStore][las] | The Android tab here is download-only. The Android-host install pipeline (signature pinning, PackageInstaller.Session, ACTION_DELETE) stays inside the LocalAndroidStore Android app — it can't run on Windows. |

---

## License

[MIT](LICENSE) — see the LICENSE file for full text.
