# Steam Game Custom Status — Project Context

Internal reference for repository-aware assistance. This file is the canonical detailed project context and should stay aligned with [`../README.md`](../README.md) and [`../.github/copilot-instructions.md`](../.github/copilot-instructions.md).

## Agent snapshot

- This is a tray-first WPF Windows app packaged as a single self-contained `exe`.
- The app is intended to be added to Steam as a `non-Steam game`.
- The main feature is renaming the Steam-displayed title by editing `shortcuts.vdf` for the **current executable path**.
- The app normally starts without opening the main window.
- Closing the main window hides it to tray instead of exiting the process.
- Only one active tray instance should survive at a time.
- Steam-launched instances have priority over normal launches when single-instance decisions are made.
- Desktop shortcuts must launch through `steam://rungameid/...`, not by starting the executable directly.
- After rename, the app may restart Steam automatically when it is safe to do so.
- User-facing instructions should prefer the published executable path, not Debug output paths.

## Do not break

### Tray and window behavior

- Preserve tray-first startup.
- Preserve hide-to-tray on window close.
- Preserve tray icon availability and the tray menu as the primary interaction model.
- Keep UI changes compact and aligned with the current dark Windows 11-style direction.

### Single-instance behavior

The current priority rules are intentional and should remain stable:

- if a normal instance is already running and a new normal instance starts, the new one takes priority and the old one exits;
- if a normal instance is already running and a Steam-launched instance starts, the new Steam-launched instance takes priority and the old one exits;
- if a Steam-launched instance is already running and a normal instance starts, the current Steam-launched instance is kept and the new launch exits;
- if a Steam-launched instance is already running and a new Steam-launched instance starts, the new Steam-launched instance takes priority.

This keeps the instance actually launched by Steam preferred when applicable.

### Steam integration invariants

- Match the Steam shortcut by the current executable path.
- Keep compatibility with `userdata/<steamid>/config/shortcuts.vdf`.
- Create a `.bak` backup before writing `shortcuts.vdf`.
- Keep desktop shortcut generation based on `steam://rungameid/<id>`.
- Prefer the published executable path in instructions and messages when referencing what should be added to Steam.

## Core runtime behavior

### Startup and tray flow

- The app is a `WinExe` and does not rely on `StartupUri`.
- `NotifyIcon` is used for tray integration.
- The tray menu contains `Open`, `Exit`, and context-dependent Steam actions.
- Double-clicking the tray icon opens the control window.
- When the main window is opened, the current Steam registration state is refreshed.

### Main window behavior

- The window shows whether the current executable is registered in Steam as a non-Steam game.
- When found, the UI shows the current Steam entry name and enables `Rename` and `Create Desktop Shortcut`.
- When not found, the rename and shortcut actions are hidden and the user is guided toward adding the published executable to Steam.
- The current compact window size is approximately `380 x 400`.
- The window is borderless, dark themed, and not shown in the taskbar.
- Inline success and warning messages are used for some actions while the window is open.

### Steam presence check

The app checks whether the current executable exists among Steam non-Steam shortcuts.

Matching rule:

- only the entry whose `Exe` value matches the path to the current running executable is considered the correct one;
- Debug builds or copies from a different folder will not match the published entry.

### Rename flow

Rename behavior is implemented around the following flow:

1. Determine the current executable path.
2. Find the Steam installation path.
3. Locate `Steam\userdata\<steamid>\config\shortcuts.vdf`.
4. Read the binary VDF structure.
5. Find the non-Steam entry matching the current executable path.
6. Update `AppName`.
7. Create a `.bak` backup.
8. Save the file back in Steam-compatible format.

### Steam restart behavior after rename

- After a successful rename, the app tries to apply the change immediately.
- If Steam is not running, the rename is saved without starting Steam.
- Automatic restart happens only if no other Steam game is currently running.
- If this exact non-Steam game is the running one, Steam may still be restarted.
- If the app is running outside Steam, only Steam is restarted and the app keeps running.
- If the app is running through Steam, the current instance closes, Steam restarts, and the app is launched again through `steam://rungameid/...`.
- If another game is running, automatic restart is skipped and the rename must be applied by a later manual Steam restart.

### Desktop shortcut behavior

- Desktop shortcut creation currently produces a `.url` file.
- The shortcut target is a `steam://rungameid/<id>` URL.
- The generated shortcut uses the current executable as its icon source.
- This matters because starting the executable directly outside Steam usually does not produce the intended non-Steam game status.

### Open-Steam behavior

When the app is not yet found in Steam:

- it first tries to open Steam directly into the add-non-Steam-game flow;
- if that fails, it falls back to opening Steam normally;
- if Steam cannot be opened automatically, the app shows the current executable path for manual addition.

## Build and publish facts

Canonical command for delivering the current executable:

```powershell
dotnet publish -c Release
```

Important current project settings from `SteamGameCustomStatus.csproj`:

- `TargetFramework = net10.0-windows`
- `RuntimeIdentifier = win-x64`
- `PublishSingleFile = true`
- `SelfContained = true`
- `UseWPF = true`
- `UseWindowsForms = true`
- `IncludeNativeLibrariesForSelfExtract = true`

Expected published executable path (relative to repository root):

```text
bin\Release\net10.0-windows\win-x64\publish\SteamGameCustomStatus.exe
```

## File map

- `App.xaml` / `App.xaml.cs` — startup, tray icon, lifecycle, dynamic tray actions
- `UI/Windows/MainWindow.xaml` / `UI/Windows/MainWindow.xaml.cs` — control window, status refresh, inline messages, and hide-to-tray behavior
- `UI/Dialogs/RenameDialog.xaml` / `UI/Dialogs/RenameDialog.xaml.cs` — rename entry dialog
- `Steam/SteamShortcutRenamer.cs` — `shortcuts.vdf` parsing, lookup, backup, update, desktop shortcut creation, and open-Steam helpers
- `Workflows/RenameShortcutWorkflow.cs` — rename workflow orchestration
- `Workflows/SteamRestartWorkflow.cs` — safe Steam restart and optional relaunch flow
- `Workflows/DesktopShortcutWorkflow.cs` — `steam://rungameid/...` desktop shortcut workflow
- `Workflows/OpenSteamAddGameWorkflow.cs` — opening Steam to add a non-Steam game
- `Infrastructure/SingleInstanceCoordinator.cs` — single-instance rules and instance priority handling
- `Infrastructure/LaunchContextDetector.cs` — detecting whether the app was launched through Steam or normally
- `Properties/AssemblyInfo.cs` — WPF theme assembly metadata
- `Assets/Icon.ico` — application icon used for publish output and tray branding
- `SteamGameCustomStatus.csproj` — packaging and publish settings

## Directory layout

- `UI/` — WPF windows and dialogs
- `Workflows/` — user-triggered flows that orchestrate UI and Steam operations
- `Steam/` — Steam-specific parsing, lookup, and persistence logic
- `Infrastructure/` — process-launch and single-instance helpers
- `Properties/` — assembly-level metadata
- `Assets/` — static application assets

## Known limitations

- The app only works with the non-Steam entry whose `Exe` exactly matches the current executable path.
- Starting the executable directly outside Steam does not provide the intended Steam running-status behavior.
- Desktop shortcut creation is currently `.url`-based, not `.lnk`-based.
- Windows autostart is not implemented.
- The tray interaction currently relies on `ContextMenuStrip`, not a custom WPF tray menu.

## Workflow expectations for future changes

- Prefer small, targeted edits over broad refactors.
- If changing Steam integration, trace the flow through `SteamShortcutRenamer`, `RenameShortcutWorkflow`, and `SteamRestartWorkflow` together.
- If changing startup or activation logic, also inspect `SingleInstanceCoordinator` and `LaunchContextDetector`.
- If changing window and tray behavior, verify both tray-first startup and hide-to-tray behavior still work.
- After code changes, validate the affected files and run an appropriate .NET build or publish command.

## Recommended future improvements

- Add explicit selection if multiple Steam shortcut candidates ever need to be supported.
- Display the current `AppName` and `rungameid` more explicitly in the UI.
- Add `.lnk` creation as an alternative to `.url`.
- Add settings persistence and Windows autostart if the product direction requires it.

