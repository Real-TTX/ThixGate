# CLAUDE.md

Guidance for working in this repository. Keep it in sync with the code: update it whenever architecture, interfaces or conventions change.

## What ThixGate is

A **remote manager** for **Windows and macOS** (like Royal TS / mRemoteNG, in the line of the Thix apps): folders, connections (RDP,
VNC, SSH) and saved credentials in one library; Remote Desktop and VNC open in a session window of ThixGate itself, SSH in the
system's terminal. **Matgate first:** the remote engine is Matgate's own (`Mat.Remote`, submodule `external/Matgate`), so a connection
means the same in both and an engine fix lands once. ThixGate is the **on-premise** alternative: it needs no Matgate. Optional:
**ThixCloud** (MatCloud, sibling repository `../MatCloud`, app id `gate`, general sync) keeps folders, connections and credentials –
never a password – the same on every device; **Matgate** accounts (app API of Matgate 0.58+: sign-in in the browser, its library, connecting through its relay or as a session on Matgate, writing connections back; see
`docs/thixgate-plan.md` in Matgate). UI language German; code, comments and identifiers English.

Stack: **.NET 10**, C#, **Avalonia 11.3** with **MatDesktop** (the desktop toolkit of MatDesign, a COPY in `src/ThixGate/MatDesktop/`),
xUnit (+ Avalonia.Headless). The scaffolding (setup, updates, autostart, tray, self test, snapshots, CI) comes from ThixSync / ThixTunnel;
the app follows MatDesign's rules DESK, TRAY, SETUP.

## Build / run / test

- Clone with the engine: `git clone --recurse-submodules …` (or `git submodule update --init`).
- `dotnet build ThixGate.slnx` – expected: 0 warnings, 0 errors. On Windows the projects target `net10.0-windows10.0.19041.0`,
  elsewhere `net10.0` (`DesktopTargetFramework`); `Mat.Remote` stays `net10.0`.
- `dotnet test ThixGate.slnx`:
  - `tests/ThixGate.Tests` (no UI): library with every check and policy, vault, import/export (.thixgate.json, .rdp), engine targets,
    SSH arguments, the sync of two computers against `FakeCloud`, Matgate (`MatgateTests`: sign-in with PKCE against a fake, library + ETag, take-over, relay, writing back, the session on Matgate over a WebSocket pair), setup/autostart/updates (`PlatformTests`).
  - `tests/ThixGate.UiTests`: the UI headless (every page light/dark, the forms, toolbar, tray, snapshot, key map, the copies of MatDesktop
    and of MatCloud's contract unchanged, no window writes a colour of its own; `THIXGATE_SCREENSHOTS=<folder>` keeps screenshots).
  - `SessionEndToEndTests` (opt-in): real sessions against Matgate's test desktop (`external/Matgate/tests/desktop`: xrdp 3389, FreeRDP
    shadow with NLA 3390, TigerVNC 5901). `docker build -t thixgate-e2e-desktop external/Matgate/tests/desktop`,
    `docker run -d -p 13389:3389 -p 13390:3390 -p 15901:5901 thixgate-e2e-desktop`, then `THIXGATE_E2E_DESKTOP=127.0.0.1`,
    `THIXGATE_E2E_PORTS=13389,13390,15901`.
  - `MatgateEndToEndTests` (opt-in): ThixGate against a real Matgate – sign-in through its pages, library, writing back, RDP/VNC sessions through its
    relay and as sessions on Matgate into the test desktop. `THIXGATE_E2E_MATGATE=<address>`, `THIXGATE_E2E_MATGATE_USER`, `THIXGATE_E2E_MATGATE_PASSWORD` (a test
    Matgate whose connections lead to the test desktop above, with the test passwords stored in Matgate, and whose user may create connections;
    `dotnet run --project external/Matgate/Matgate` with `MATGATE_DATA_DIR`,
    `MATGATE_ADMIN_USER/_PASSWORD`, `ASPNETCORE_URLS=http://127.0.0.1:18200` does).
  Tests never touch real data, the Keychain, the registry or LaunchAgents (`TestData`).
- **Snapshots:** `dotnet run --project src/ThixGate -- --snapshot docs/images` draws every window headless (light, dark, 1x) from
  `SampleData` (Core: folders Büro, Kunden › Müller GmbH, Labor; credentials; RDP/VNC/SSH connections; a Matgate) with pinned clock
  12.11.2026 14:32, version 1.0.0, computer ANNA-PC. A new page: add it to `Snapshot.cs` (and `FileNames`).
- `ThixGate.exe --self-test <report>` checks the published (trimmed) program; CI runs it after every Windows publish.
- **The MatCloud contract** (`src/ThixGate.Core/MatCloud/`) is a COPY: change it in MatCloud, copy it with
  `node ../MatCloud/tools/contract-copy.js src/ThixGate.Core`.
- **MatDesktop** (`src/ThixGate/MatDesktop/`) is a COPY from MatDesign: never edit it here.
- **Mat.Remote** is Matgate's: change it in Matgate (`Mat.Remote/`), push there, then move the submodule
  (`cd external/Matgate && git fetch && git checkout <commit>`; commit the new pointer here). Its namespaces are `Matgate.Display.*`;
  the way in is `ScreenBackends.Create(target, host)`.
- Icons: `cd tools && npm install && node render-icons.js` renders `branding/*.svg` into `branding/png` and `src/ThixGate/Assets`.
- `THIXGATE_DATA_DIR=<folder>` runs an own instance with its own data.
- `scripts/package-macos.sh` builds `ThixGate.app`, the update zip and the dmg.

## Map

```
external/Matgate        submodule: Matgate; ThixGate uses only Mat.Remote/ (RDP, VNC, sound, clipboard, H.264 – no ASP.NET)
src/ThixGate.Core       (no UI) models, business layer (static classes), infrastructure
  MatCloud/             COPY of MatCloud's contract 1.8.0 (CloudApps.Gate, general sync, sign-in, CloudHandoff)
  Models/               Library (RemoteFolder, RemoteConnection (+ SessionOnMatgate), StoredCredential, ConnectionProtocol, ConnectVia, SessionQuality,
                        LibraryData + Tombstone + SyncMark, ConnectionQuery, PagedList), AppSettings (MatgateAccount, CloudSettings), MatgateLibrary
                        (+ MatgateConnectionDraft),
                        InstallerConfig, BusinessException
  Business/             LibraryService (folders + connections: checks, normalising per protocol, search/filter/sort/pages, tombstones,
                        Changed; Tree: the rows of the tree column), CredentialService (the vault: passwords via TokenStore, never synced), SessionTargets (connection →
                        Mat.Remote ScreenTarget; BackendAsync: engine or MatgateScreen), SshLauncher (system terminal, no password on a command line), TransferService
                        (*.thixgate.json without passwords, *.rdp), MatgateService (accounts, /health, browser sign-in to /app/authorize,
                        library copy, take-over, writing back /app/api/connections), MatgateRelay (127.0.0.1 port → WebSocket
                        /app/api/relay/<id>), MatgateScreen (IScreenBackend of a session on Matgate: /app/api/screen/<id>, screen.tiles
                        = BGRX in zlib, acknowledged; keys as screen.input; screen.auth answered to Matgate), CloudSyncService (sign-in,
                        loop push → pull → long poll) + CloudSyncItems (collections folders/connections/credentials), SettingsService,
                        AccessPolicy, AutostartService, InstallService, InstallerOptions, UpdateService, MacAppBundle, SampleData
  Infrastructure/       LibraryStore (library.json + vault.json, atomic writes, the cloud's pending rows), MatgateStore (matgate/<account>.json),
                        LoopbackCallback (the browser comes back to 127.0.0.1), SettingsStore, TokenStore
                        (DPAPI / Keychain, per SecretKind: Cloud, Vault, Matgate), CloudClient, AppPaths, AppInfo, AppClock, Log,
                        ProblemReports
  Platform/             WindowActivation, Windows/ShellLink, Windows/EnvironmentChange
src/ThixGate            (Avalonia app)
  App / Program         start-up, setup mode, single instance, tray, cloud sync; the end closes the sessions
  MainWindow            MatTitleBar (open sessions in the middle), MatNav: Verbindungen, Ordner, Zugangsdaten, Matgate | Einstellungen;
                        foot: cloud state, version = About; beside it the tree column (ConnectionTree, width at a splitter, hidden with +L,
                        remembered). Forms on top with a back stack (Zurück/Esc). Ctrl/Cmd+1…4, +, , +N, +F, +B, +L
  Pages/                ConnectionsPage + ConnectionForm (Allgemein | Anmeldung | Anzeige | Gateway | Notizen), FolderPages (list + form),
                        CredentialPages (list + form with Verwendung), MatgatePages (list + form: Allgemein | Verbindungen),
                        MatgateConnectionForm (a connection IN Matgate: create/change/delete as Matgate allows), SettingsPage (Allgemein | Sitzungen |
                        ThixCloud | Updates | Erweitert), AboutPage
  Sessions/             SessionWindow (IScreenHost of Mat.Remote: draws the engine's picture into a WriteableBitmap on demand, pointer,
                        keys, sign-in dialog), KeyMap (PhysicalKey → scancode for RDP, keysym for VNC), SessionLauncher, NullScreenHost
  Controls/             ONE code base per control: ConnectionTree (the library as folders and connections, LibraryService.Tree),
                        ListLayout<T> (THE list page), EditLayout (THE create/update page), ListToolbar,
                        Pager, ActionBar, FormFields, FormErrors, InfoBar, IconLabel, StatePill/EmptyState, DataTable, Formats, Protocols
  Dialogs/ Setup/ Tray/ ConfirmDialog, SignInDialog, ProblemReportDialog; ThixGateSetup (Windows); TrayIcon (favourites + recent to connect)
tests/ThixGate.Tests    core, sync, platform
tests/ThixGate.UiTests  the UI headless, the end-to-end sessions (opt-in)
```

## Conventions

- **Microsoft C# coding conventions**, file-scoped namespaces, `_camelCase` fields, human-readable names, early returns, no needless nesting.
- **Business layer = static classes.** The UI never touches LibraryStore, files, registry or HTTP; validation **and permission checks**
  (AccessPolicy: LockConfiguration, DisableCloud, DisableUpdates, DisableMatgate, DisableSavedPasswords) live in the business layer.
  Errors are `BusinessException(message, field)`; forms show them at the field (`EditLayout.Run`).
- **Secrets:** a password lives only in the vault (TokenStore: Windows DPAPI value in vault.json, macOS Keychain). Never in library.json,
  an export, the cloud, a log or a command line. The cloud gets `HasPassword = false`; "is a password here" is per computer.
- **Sync rules:** general sync of MatCloud, app `gate`; last writer wins by UpdateDate; deletions are tombstones until the cloud has them;
  "last used" stays per computer; a Matgate account is per computer (unknown here → the connection goes direct). "Über Matgate" names
  the account AND the connection in that Matgate (`MatgateConnectionId`): the relay opens only host and port of a Matgate connection.
  The Matgate token and library copy never sync; Matgate never gets a password from ThixGate (writing back sends what a connection is;
  a session on Matgate signs in with Matgate's login - only what the user types when Matgate asks goes there, and is not kept here).
- **Engine:** never fork Mat.Remote here – a change goes to Matgate first ("Matgate first") and arrives by moving the submodule.
- **UI rules (the owner's):** CRUD = list page (`ListLayout`) + create/update page with delete (`EditLayout`); toolbar above lists
  (search, filter, sort); pager below the list; actions below, left-aligned, positive to negative, delete with a gap; buttons in one row
  (Speichern, …, Zurück … Löschen); icons where they help; reusable controls in Controls/ (several on one page work: no static state);
  hide what does not apply (domain only for RDP, key file only for SSH, display tab not for SSH); tabs for areas of one item; cards keep
  their distance; About from the version bottom left; the library as a tree beside the menu on every page; an ActionBar that does not fit shows
  the icons of its buttons (after the first) before it wraps. Colours only from MatDesktop's theme; ThixGate's accent is `#4B3FCF`.
- **Platforms:** one code base; differences behind `OperatingSystem.IsWindows()/IsMacOS()`. macOS cannot be checked from Windows: CI
  builds and tests it.
- Edit text files with the editor tools (UTF-8): Windows PowerShell 5.1 `Get-Content`/`Set-Content` double-encode umlauts.

## Versioning, branches, CI

- `Directory.Build.props` holds major/minor. CI sets `Channel`, `BuildNumber` (GitHub run number), `BuildDate`: release
  `0.1.42-20261126` (main), nightly `nightly-43-20261126` (dev), local `local-20261126` (everything else).
- Branches: `main` = release, `dev` = nightly. **Commits are authored by Matthias Schmoldt only – no co-author trailers, no other
  names (also not in branch names), no "generated with" lines.**
- `.github/workflows/desktop.yml` (every branch; publishing only main/dev): checkout with submodules; Windows (`dotnet test`, publish
  win-x64 single file, self test, `ThixGateSetup.exe`), macOS (test, publish osx-arm64/osx-x64, `scripts/package-macos.sh`), publish
  (manifest `thixgate-update.json` with the packages that were built; rolling releases `release-channel` / `nightly-channel`; main also
  `v<version>`). `publish` needs only a green Windows job.
