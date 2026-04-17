# Steam Game Custom Status

Minimal tray-first Windows app for running as a Steam `non-Steam game` and controlling the title Steam shows while that shortcut is active.

## What the app does

- starts in the system tray without opening the main window
- keeps the app alive in the tray when the window is closed
- detects whether the **current published executable path** is already registered in Steam
- finds the matching Steam shortcut by the current executable path inside `userdata\<steamid>\config\shortcuts.vdf`
- creates a `.bak` backup before writing `shortcuts.vdf`
- renames the matching non-Steam entry so Steam shows the updated title
- can restart Steam after rename when it is safe to apply the new name immediately
- creates desktop `.url` shortcuts that launch through `steam://rungameid/...`
- opens Steam to the add-non-Steam-game flow when the app is not yet registered
- keeps a single active tray instance and prefers the Steam-launched instance when needed

## What it does not do

- it does **not** match arbitrary copies or Debug builds from other folders
- it does **not** create `.lnk` desktop shortcuts; it currently creates `.url` shortcuts
- it does **not** exit when the main window is closed unless the user explicitly chooses `Exit`

## How it works in practice

1. Publish the app.
2. Add the published executable to Steam as a **non-Steam game**.
3. Launch it from Steam when you want Steam status integration.
4. Use the tray menu or compact window to rename the Steam entry or create a desktop shortcut.

If the app is not yet registered in Steam, it can open Steam to the add-game flow and shows the current executable path for manual selection.

## Important behavior notes

### Tray and window behavior

- Primary interaction is through the tray icon and tray menu.
- Double-clicking the tray icon opens the control window.
- Closing the window hides it back to tray instead of exiting the app.

### Single-instance behavior

The single-instance rules are intentional:

- a new normal launch replaces an older normal launch
- a Steam launch replaces an older normal launch
- an existing Steam-launched instance stays active when a normal launch is attempted later
- a newer Steam launch replaces an older Steam launch

### Steam rename behavior

- The app only renames the shortcut whose `Exe` value matches the path of the currently running executable.
- If Steam is running and no other Steam game is active, the app can restart Steam automatically so the new name is applied right away.
- If another Steam game is currently running, the rename is saved but Steam restart is skipped.

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

## Repository pointers

- `App.xaml` / `App.xaml.cs` — startup, tray icon, lifecycle, dynamic tray actions
- `MainWindow.xaml` / `MainWindow.xaml.cs` — compact control window and hide-to-tray behavior
- `SteamShortcutRenamer.cs` — `shortcuts.vdf` lookup, backup creation, rename, and shortcut generation
- `SteamRestartWorkflow.cs` — rename + safe Steam restart / relaunch flow
- `SingleInstanceCoordinator.cs` — single-instance coordination and launch priority rules

For deeper repository-specific context, see [`docs/project-context.md`](docs/project-context.md).

