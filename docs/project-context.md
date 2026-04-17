# Steam Game Custom Status — Project Context

Internal reference for repository-aware assistance. This file is the canonical detailed project context and should stay aligned with [`../README.md`](../README.md).

## Agent snapshot

- This is a tray-first WPF Windows app packaged as a single self-contained `exe`.
- The app is intended to be added to Steam as a `non-Steam game`.
- The main feature is renaming the Steam-displayed title by editing `shortcuts.vdf` for the **current executable path**.
- The app normally starts without opening the main window.
- Closing the main window hides it to tray instead of exiting the process.
- Only one active tray instance should survive at a time.
- Steam-launched instances have priority over normal launches when single-instance decisions are made.
- Desktop shortcuts must launch through `steam://rungameid/...`, not by starting the executable directly.
- After rename, the app may restart Steam automatically when it is safe to do so, using a helper-mode relaunch flow.
- The UI exposes Steam registration state, the current Steam name, and an Active/Inactive indicator for Steam presence.
- A `Launch via Steam` action is available when the shortcut exists but the current instance was started outside Steam.
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
- Keep the current backup behavior that replaces `shortcuts.vdf.bak` and cleans up legacy timestamped backup files.
- Keep desktop shortcut generation based on `steam://rungameid/<id>`.
- Preserve the `Launch via Steam` flow based on `steam://rungameid/<id>`.
- Prefer the published executable path in instructions and messages when referencing what should be added to Steam.

## Core runtime behavior

### Startup and tray flow

- The app is a `WinExe` and does not rely on `StartupUri`.
- `NotifyIcon` is used for tray integration.
- The tray menu contains `Open`, `Exit`, and context-dependent Steam actions.
- The tray icon mirrors Steam activity: white while active, gray while inactive.
- When the current executable is registered in Steam, the tray can expose `Rename`, `Create Desktop Shortcut`, and optionally `Launch via Steam`.
- Double-clicking the tray icon opens the control window.
- When the main window is opened, the current Steam registration state is refreshed.
- While the app is currently active in Steam, a low-frequency timer rechecks activity so tray and indicator can fall back to inactive after Steam stops reporting the shortcut as running.
- Helper-mode launches used for rename/restart should exit early from normal startup handling.

### Main window behavior

- The window shows whether the current executable is registered in Steam as a non-Steam game.
- When found, the UI shows the current Steam entry name and enables `Rename` and `Create Desktop Shortcut`.
- When found outside a Steam launch, the UI also exposes `Launch via Steam`.
- When not found, the rename and shortcut actions are hidden and the user is guided toward adding the published executable to Steam.
- The UI shows a green/red Steam activity indicator with explanatory tooltip text.
- The missing-state action opens Steam to the add-game flow and surfaces the current executable path for manual selection.
- The current compact window size is approximately `380 x 444`.
- The window is borderless, dark themed, and not shown in the taskbar.
- Inline success and warning messages are used for some actions while the window is open.

### Steam presence check

The app checks whether the current executable exists among Steam non-Steam shortcuts.

Matching rule:

- only the entry whose `Exe` value matches the path to the current running executable is considered the correct one;
- Debug builds or copies from a different folder will not match the published entry.

Steam activity rule:

- the shortcut is considered active when either the app was launched through Steam or Steam's `RunningAppID` matches the matched shortcut `appid`.
- for live UI/tray updates, a Steam-launched session may initially be treated as active and then rechecked against `RunningAppID` on a low-frequency timer while active.

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

Rename dialog UX notes:

- the rename dialog keeps manual free-text entry as the primary action;
- while typing, it can surface offline game-name suggestions from an embedded curated multi-console exclusives catalog spanning current platforms back to the PS3/Xbox 360 era;
- suggestions are assistive only and must never block custom naming or successful rename submission;
- the suggestion layer is intentionally extensible so richer online or cached providers can be added later without rewriting Steam rename logic.

Lookup and write notes:

- the parser reads Steam's binary VDF object structure directly;
- path normalization trims surrounding quotes and launch arguments before comparing the executable path;
- a rename only touches entries whose `Exe` field resolves to the current executable path.

### Steam restart behavior after rename

- After a successful rename, the app tries to apply the change immediately.
- If Steam is not running, the rename is saved without starting Steam.
- Automatic restart happens only if no other Steam game is currently running.
- If this exact non-Steam game is the running one, Steam may still be restarted.
- If the app is running outside Steam, only Steam is restarted and the app keeps running.
- If the app is running through Steam, the current instance closes, Steam restarts, and the app is launched again through `steam://rungameid/...`.
- If another game is running, automatic restart is skipped and the rename must be applied by a later manual Steam restart.
- The restart is coordinated by relaunching the same executable in hidden helper mode with internal arguments such as `--steam-restart-helper`, `--new-name`, and optional relaunch/wait parameters.

### Desktop shortcut behavior

- Desktop shortcut creation currently produces a `.url` file.
- The shortcut target is a `steam://rungameid/<id>` URL.
- The generated shortcut uses the current executable as its icon source.
- The shortcut file name is based on the current executable file name and sanitized for Windows file-system rules.
- If a `.url` with the same name already exists and points to the same `steam://rungameid/...` target, the workflow reports success without creating a duplicate.
- This matters because starting the executable directly outside Steam usually does not produce the intended non-Steam game status.

### Launch-via-Steam behavior

- The `Launch via Steam` action is only relevant when the current executable is already registered and the current instance was started outside Steam.
- It launches `steam://rungameid/<id>` for the current shortcut and then exits the current instance so Steam owns the running session.
- Failures in this flow are surfaced as inline warnings in the main window.

### Open-Steam behavior

When the app is not yet found in Steam:

- it first tries to open Steam directly into the add-non-Steam-game flow;
- if that fails, it falls back to opening Steam normally;
- if Steam cannot be opened automatically, the app shows the current executable path for manual addition.

### Single-instance implementation notes

- Single-instance coordination is implemented with a per-user local mutex and a named pipe server/client exchange.
- New launches send a small JSON startup message describing whether they were Steam-launched.
- The existing instance either activates itself or yields ownership so the new instance can become primary.
- Steam-launched instances intentionally win over normal launches.

### Launch-context detection notes

- Steam launch detection walks the parent-process chain up to a limited depth.
- It uses `NtQueryInformationProcess` to read parent process IDs and checks whether any ancestor process is `steam`.

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

- `App.xaml` / `App.xaml.cs` — startup, tray icon, lifecycle, dynamic tray actions, and Steam relaunch exit handling
- `UI/Windows/MainWindow.xaml` / `UI/Windows/MainWindow.xaml.cs` — control window, status refresh, Steam activity indicator, inline messages, and hide-to-tray behavior
- `UI/Dialogs/RenameDialog.xaml` / `UI/Dialogs/RenameDialog.xaml.cs` — rename entry dialog
- `Suggestions/` — rename-dialog game-name suggestion contracts, embedded catalog source, and aggregation service
- `Steam/SteamShortcutRenamer.cs` — `shortcuts.vdf` parsing, lookup, backup, update, activity detection, desktop shortcut creation, and open-Steam helpers
- `Workflows/RenameShortcutWorkflow.cs` — rename workflow orchestration
- `Workflows/SteamRestartWorkflow.cs` — safe Steam restart and optional relaunch flow, including hidden helper mode
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
- Steam activity detection depends on Steam exposing the expected `RunningAppID` registry value or on the app being started through Steam.

## Workflow expectations for future changes

- Prefer small, targeted edits over broad refactors.
- If changing Steam integration, trace the flow through `SteamShortcutRenamer`, `RenameShortcutWorkflow`, and `SteamRestartWorkflow` together.
- If changing rename suggestions, preserve compact keyboard-friendly behavior and keep manual custom names working even when suggestion sources are unavailable.
- If changing startup or activation logic, also inspect `SingleInstanceCoordinator` and `LaunchContextDetector`.
- If changing window and tray behavior, verify both tray-first startup and hide-to-tray behavior still work.
- After code changes, validate the affected files and run an appropriate .NET build or publish command.

## Recommended future improvements

- Add explicit selection if multiple Steam shortcut candidates ever need to be supported.
- Display the current `AppName`, `appid`, and `rungameid` more explicitly in the UI.
- Add `.lnk` creation as an alternative to `.url`.
- Add settings persistence and Windows autostart if the product direction requires it.

