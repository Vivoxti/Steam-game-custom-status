# Steam Game Custom Status

Minimal tray-first Windows app for running as a Steam `non-Steam game` and controlling the title Steam shows while that shortcut is active.

## What the app does

- starts in the system tray without opening the main window
- keeps the app alive in the tray when the window is closed
- detects whether the **current executable path** is already registered in Steam
- finds the matching Steam shortcut by the current executable path inside `userdata\<steamid>\config\shortcuts.vdf`
- shows whether that shortcut is currently **Active in Steam** or **Inactive in Steam**
- shows the current Steam entry name when the shortcut is found
- lets you rename the matching non-Steam entry
- creates a `.bak` backup before writing `shortcuts.vdf`
- can restart Steam automatically after rename when it is safe to apply the new name immediately
- can relaunch itself through `steam://rungameid/...` after the rename flow when the app was originally started from Steam
- creates desktop `.url` shortcuts that launch through `steam://rungameid/...`
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
- it does **not** create `.lnk` desktop shortcuts; it currently creates `.url` shortcuts
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
- Closing the window hides it back to tray instead of exiting the app.
- The control window is borderless, dark themed, hidden from the taskbar, and currently sized around `380 x 444`.
- Inline success and warning messages are used while the window is open.

### Steam registration and active-state behavior

- The app only works with the non-Steam shortcut whose `Exe` value matches the path of the currently running executable.
- The Active/Inactive indicator is green when the app was launched by Steam or when Steam reports that the matched shortcut is the current `RunningAppID`.
- Starting a different copy of the app from another folder creates a path mismatch and will not control the published Steam entry.

### Rename and Steam restart behavior

- A rename updates the matching shortcut entry and writes a fresh `shortcuts.vdf.bak` backup before saving.
- If Steam is not running, the rename is saved without starting Steam.
- If Steam is running and no other Steam game is active, the app starts a helper relaunch flow so the name is applied immediately.
- If the app was launched from Steam, that helper flow can close the current instance, restart Steam, and relaunch the app through `steam://rungameid/...`.
- If another Steam game is currently running, the rename is saved but Steam restart is skipped.

### Desktop shortcut and add-game behavior

- Desktop shortcut creation produces a `.url` file on the Desktop.
- The shortcut uses `steam://rungameid/<id>` and the current executable as its icon source.
- The shortcut file name is based on the current executable name.
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

## Repository pointers

- `App.xaml` / `App.xaml.cs` — startup, tray icon, lifecycle, dynamic tray actions, and Steam relaunch exit handling
- `UI/Windows/MainWindow.xaml` / `UI/Windows/MainWindow.xaml.cs` — compact control window, status refresh, active/inactive indicator, and inline messages
- `UI/Dialogs/RenameDialog.xaml` / `UI/Dialogs/RenameDialog.xaml.cs` — rename dialog UI
- `Steam/SteamShortcutRenamer.cs` — `shortcuts.vdf` lookup, backup creation, rename, launch metadata, desktop shortcut generation, and Steam-opening helpers
- `Workflows/RenameShortcutWorkflow.cs` — rename dialog and result handling
- `Workflows/DesktopShortcutWorkflow.cs` — desktop shortcut flow
- `Workflows/OpenSteamAddGameWorkflow.cs` — open-Steam add-game flow
- `Workflows/SteamRestartWorkflow.cs` — rename + safe Steam restart / relaunch helper flow
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

