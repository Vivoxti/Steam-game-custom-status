# Steam Game Custom Status

Minimal tray-first Windows app for running as a Steam `non-Steam game` and controlling the title Steam shows while that shortcut is active.

## What the app does

- starts in the system tray without opening the main window
- keeps the app alive in the tray when the window is closed
- mirrors the Steam activity state in the tray icon: white while active, gray while inactive
- detects whether the **current executable path** is already registered in Steam
- finds the matching Steam shortcut by the current executable path inside `userdata\<steamid>\config\shortcuts.vdf`
- shows whether that shortcut is currently **Active in Steam** or **Inactive in Steam**
- shows the current Steam entry name when the shortcut is found
- lets you rename the matching non-Steam entry
- offers hybrid game-name suggestions in the rename dialog from a curated offline multi-console exclusives catalog plus Steam Store search when the network is available
- creates a `.bak` backup before writing `shortcuts.vdf`
- can restart Steam automatically after rename when it is safe to apply the new name immediately
- can relaunch itself through `steam://rungameid/...` after the rename flow when the app was originally started from Steam
- uses a hidden external restart helper and exit cleanup guard so the app executable itself is less likely to leave Steam stuck on an active or closing status
- creates desktop `.lnk` shortcuts that launch through `steam://rungameid/...`
- offers a `Launch via Steam` action when the shortcut exists but the app was started outside Steam
- opens Steam to the add-non-Steam-game flow when the app is not yet registered
- keeps a single active tray instance and prefers the Steam-launched instance when needed

## Main UI and tray actions

The compact window and tray menu are intentionally small and focused.

When the current executable **is registered in Steam**:

- the app shows a success card with the current Steam name
- `Rename` is available
- `Create Desktop Shortcut` is available
- `Launch via Steam` is available when the current instance was not launched from Steam

When the current executable **is not registered in Steam**:

- the app shows a warning card with guidance
- `Open Steam to Add Game` is available
- the current executable path is surfaced so you can add the correct published `.exe`

## What it does not do

- it does **not** match arbitrary copies or Debug builds from other folders
- it does **not** start the non-Steam game shortcut directly; desktop shortcuts still route through `steam://rungameid/...`
- it does **not** exit when the main window is closed unless the user explicitly chooses `Exit`
- it does **not** rename unrelated Steam shortcuts; matching is strictly by the current executable path

## Recommended usage flow

1. Publish the app.
2. Add the published executable to Steam as a **non-Steam game**.
3. Launch it from Steam whenever you want the Steam running-status integration.
4. Use the tray menu or compact window to rename the Steam entry, create a desktop shortcut, or relaunch through Steam.

If the app is not yet registered in Steam, it can open Steam to the add-game flow and shows the current executable path for manual selection.

## Important behavior notes

### Tray and window behavior

- Primary interaction is through the tray icon and tray menu.
- Double-clicking the tray icon opens the control window.
- The tray icon is white while the current shortcut is active in Steam and gray while inactive.
- Closing the window hides it back to tray instead of exiting the app.
- The control window is borderless, dark themed, hidden from the taskbar, and currently sized around `380 x 444`.
- Inline success and warning messages are used while the window is open.

### Steam registration and active-state behavior

- The app only works with the non-Steam shortcut whose `Exe` value matches the path of the currently running executable.
- The Active/Inactive indicator is green when the app was launched by Steam or when Steam reports that the matched shortcut is the current `RunningAppID`.
- While the app is currently active, a low-frequency background recheck keeps the tray icon and indicator aligned if Steam later stops reporting the shortcut as running.
- Starting a different copy of the app from another folder creates a path mismatch and will not control the published Steam entry.

### Rename and Steam restart behavior

- A rename updates the matching shortcut entry and writes a fresh `shortcuts.vdf.bak` backup before saving.
- The rename dialog offers hybrid suggestions while you type: immediate offline matches from the embedded curated console-exclusive list plus online Steam Store matches when available.
- Online lookup uses the public Steam Store search API, so the desktop app does not need OAuth or a heavy auth flow just to provide live title suggestions.
- Suggestion refresh is debounced, cancels stale requests, uses a short network timeout, and keeps a small in-memory cache of recent exact-query results.
- Suggestions are assistive only: you can always ignore them and type any custom Steam name you want.
- If the network or the Steam Store endpoint is unavailable, the dialog keeps working with offline suggestions only and manual custom names still work normally.
- If Steam is not running, the rename is saved without starting Steam.
- If Steam is running and no other Steam game is active, the app saves the rename immediately and then starts a hidden external restart helper so the name is applied without relaunching the app executable in helper mode.
- If the app was launched from Steam, that helper can close the current instance, restart Steam, and relaunch the app through `steam://rungameid/...`.
- If another Steam game is currently running, the rename is saved but Steam restart is skipped.
- On a real app exit, the app also schedules a best-effort cleanup for a stale `RunningAppID` that still points at this shortcut after the process is gone.

### Desktop shortcut and add-game behavior

- Desktop shortcut creation produces a `.lnk` file on the Desktop so Windows can pin it more reliably.
- The shortcut still launches `steam://rungameid/<id>` and uses the current executable as its icon source.
- Under the hood, the `.lnk` points at `explorer.exe` with the `steam://rungameid/...` URI as its argument so the launch still goes through Steam instead of starting the app shortcut directly.
- The shortcut file name is based on the current executable name.
- If a matching legacy `.url` shortcut from an older build already exists, the app replaces it with the new `.lnk` shortcut.
- If Steam cannot open the add-game flow directly, the app falls back to opening Steam normally and shows manual instructions.

### Single-instance behavior

The single-instance rules are intentional:

- a new normal launch replaces an older normal launch
- a Steam launch replaces an older normal launch
- an existing Steam-launched instance stays active when a normal launch is attempted later
- a newer Steam launch replaces an older Steam launch

## Tech stack

- `.NET 10`
- `WPF`
- `Windows Forms NotifyIcon`

## Build and publish

Canonical executable build:

```powershell
dotnet publish -c Release
```

Published executable (relative to repository root):

```text
bin\Release\net10.0-windows\win-x64\publish\SteamGameCustomStatus.exe
```

Current publish-related project settings:

- `TargetFramework = net10.0-windows`
- `RuntimeIdentifier = win-x64`
- `PublishSingleFile = true`
- `SelfContained = true`
- `UseWPF = true`
- `UseWindowsForms = true`
- `IncludeNativeLibrariesForSelfExtract = true`

## Release verification checklist

Before shipping a build, verify the following against the published executable at
`bin\Release\net10.0-windows\win-x64\publish\SteamGameCustomStatus.exe`:

- publish succeeds with `dotnet publish -c Release`
- the app starts in the tray without opening the main window
- closing the main window hides it back to tray instead of exiting
- the tray icon stays gray while inactive and turns white while the current shortcut is active in Steam
- the main window correctly distinguishes between the registered and missing-in-Steam states
- rename still creates `shortcuts.vdf.bak` and preserves manual custom naming
- `Launch via Steam` is only shown when the shortcut exists and the app was started outside Steam
- desktop shortcut creation still targets `steam://rungameid/...`

## Repository pointers

- `App.xaml` / `App.xaml.cs` — startup, tray icon, lifecycle, dynamic tray actions, and Steam relaunch exit handling
- `UI/Windows/MainWindow.xaml` / `UI/Windows/MainWindow.xaml.cs` — compact control window, status refresh, active/inactive indicator, and inline messages
- `UI/Dialogs/RenameDialog.xaml` / `UI/Dialogs/RenameDialog.xaml.cs` — rename dialog UI
- `Suggestions/` — game-name suggestion sources and aggregation for the rename dialog
- `Steam/SteamShortcutRenamer.cs` — `shortcuts.vdf` lookup, backup creation, rename, launch metadata, desktop shortcut generation, and Steam-opening helpers
- `Workflows/RenameShortcutWorkflow.cs` — rename dialog and result handling
- `Workflows/DesktopShortcutWorkflow.cs` — desktop shortcut flow
- `Workflows/OpenSteamAddGameWorkflow.cs` — open-Steam add-game flow
- `Workflows/SteamRestartWorkflow.cs` — rename + safe Steam restart / relaunch orchestration
- `Infrastructure/SteamLifecycleGuard.cs` — hidden PowerShell helpers for safe Steam restart and stale running-state cleanup
- `Infrastructure/SingleInstanceCoordinator.cs` — single-instance coordination and launch priority rules
- `Infrastructure/LaunchContextDetector.cs` — detection of Steam-launched vs normal starts

## Project structure

- `UI/` — WPF windows and dialogs
- `Workflows/` — user-triggered application flows such as rename, Steam restart, relaunch, and shortcut creation
- `Steam/` — Steam-specific file parsing, matching, persistence, and helper operations
- `Infrastructure/` — launch-context and single-instance coordination helpers
- `Properties/` — assembly metadata
- `Assets/` — application icon and other static assets

## More context

For deeper repository-specific context, safe-change boundaries, and implementation notes, see [`docs/project-context.md`](./docs/project-context.md).

