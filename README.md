# Steam Game Custom Status

A Windows app designed to run from the system tray and be used as a `non-Steam game`, so Steam can show the running-game status and let you rename the displayed title.

At the moment, this is a tray-first WPF utility bundled into a single `exe`, with a minimal Windows 11-style control window, real Steam entry renaming through `shortcuts.vdf`, and desktop shortcut creation that launches the app through `steam://rungameid/...`.

## What is implemented

- The app starts as a `WinExe` without automatically showing the main window.
- After startup, the app lives in the system tray through `NotifyIcon`.
- Only one active tray instance is supported at a time.
- The tray context menu contains:
  - `Open`
  - `Rename` — only if the current `exe` is found in Steam as a `non-Steam game`
  - `Create Desktop Shortcut` — only if the current `exe` is found in Steam as a `non-Steam game`
  - `Exit`
- Double-clicking the tray icon opens the control window.
- Closing the window does not exit the app; it hides it back to the tray.
- The app window uses a dark, minimal Windows 11-style theme.
- The main window shows whether the current `exe` is found in Steam as a `non-Steam game`.
- The window displays two different states:
  - `Added to Steam as a non-Steam game`
  - `Not found in Steam as a non-Steam game`
- When the entry is not found, the window shows a hint and a separate button to open Steam for adding the game.
- The main window actions (`Rename`, `Create Desktop Shortcut`, `Hide to Tray`) are available only if the current `exe` is already found in Steam.
- A custom `Icon.ico` is connected.
- The icon is applied both to the `exe` itself and to the tray icon.
- `Single-file self-contained publish` for `win-x64` is configured.
- Real renaming of the `non-Steam` Steam entry is implemented through `userdata/<steamid>/config/shortcuts.vdf`.
- A `.bak` backup is created before changing `shortcuts.vdf`.
- The correct entry is located by the path of the current `exe`.
- Desktop shortcut creation was added so the shortcut launches the entry through `steam://rungameid/...` instead of starting the `exe` directly.
- Opening Steam was added, with an attempt to jump directly to the `non-Steam game` add flow if the current `exe` is not yet found in Steam.
- A single-instance coordinator was added: a new launch does not leave a second tray icon behind and instead consistently activates or replaces the already running instance.
- After a successful rename, the app now automatically restarts Steam if no other game is currently running there; if the app was launched through Steam, it is then automatically launched again with the new name.

## Interface

The main window is compact and minimal:

- compact status cards for the Steam registration state
- a custom title bar and a close button that hides the window to the tray
- a primary accent action button `Rename` when the app is already found in Steam
- secondary actions `Create Desktop Shortcut` and `Hide to Tray` when the app is already found in Steam
- a separate `Open Steam to Add Game` button when the app is not yet found in Steam

Current window size:

```text
380 x 420
```

## How it works

### Renaming

The app:

1. Determines the path to the current running `exe`.
2. Finds the Steam folder through the Windows registry.
3. Looks for files:

```text
Steam\userdata\<steamid>\config\shortcuts.vdf
```

4. Reads the binary VDF.
5. Finds the `non-Steam` entry whose `Exe` field matches the path to the current `exe`.
6. Changes `AppName`.
7. Saves the file back in the correct `shortcuts.vdf` format.

### Desktop shortcut

The `Create Desktop Shortcut` button creates a `.url` file on the desktop with an address like this:

```text
steam://rungameid/<id>
```

This shortcut launches the app through Steam. That matters because when the `exe` is started directly outside Steam, the `non-Steam game` status is usually not shown.

### Steam presence check

When the main window is opened, the app automatically checks whether the current `exe` exists among Steam `non-Steam` entries.

If the entry is found:

- a green `Added to Steam as a non-Steam game` status is shown;
- the current Steam entry name is also displayed;
- the actions `Rename`, `Create Desktop Shortcut`, and `Hide to Tray` are available;
- the tray menu shows `Rename` and `Create Desktop Shortcut`.

If the entry is not found:

- a warning `Not found in Steam as a non-Steam game` status is shown;
- a hint explains that you need to add the published `exe`, not a Debug build or a copy from another folder;
- the rename and shortcut buttons are hidden;
- the `Open Steam to Add Game` button remains available;
- `Rename` and `Create Desktop Shortcut` are hidden in the tray menu.

The `Open Steam to Add Game` button:

- first tries to open Steam directly in the `non-Steam game` add flow;
- if that does not work, it simply opens Steam;
- if Steam cannot be opened automatically, it shows the path to the current `exe`, which you can add manually.

## What you can already do

### 1. Add the app to Steam

Add the published `exe` to Steam as a `non-Steam game`.

Recommended file:

```text
C:\Vivoderin\SteamGameCustomStatus\bin\Release\net10.0-windows\win-x64\publish\SteamGameCustomStatus.exe
```

### 2. Rename the displayed title

- Launch the app.
- Make sure the window shows the status `Added to Steam as a non-Steam game`.
- Click `Rename` in the window or in the tray menu.
- Enter the new name.
- If no other game is currently running in Steam, the app will restart Steam automatically to apply the new name.
- If the app was launched through Steam, after Steam restarts it will open again automatically with the new name.
- If another game is running in Steam at that moment, automatic Steam restart will be skipped and you will need to do it manually later.

### 3. Create a desktop shortcut

- Launch the app.
- Make sure the window shows the status `Added to Steam as a non-Steam game`.
- Click `Create Desktop Shortcut`.
- A `.url` file will appear on the desktop; use it to launch through Steam.

### 4. If the app has not been added to Steam yet

- Launch the app.
- If you see the status `Not found in Steam as a non-Steam game`, click `Open Steam to Add Game`.
- If Steam does not open the required window automatically, in Steam choose `Games → Add a Non-Steam Game to My Library`.
- Add exactly this published file:

```text
C:\Vivoderin\SteamGameCustomStatus\bin\Release\net10.0-windows\win-x64\publish\SteamGameCustomStatus.exe
```

- After adding it, open the app main window again: the status should switch to the found entry state.

## Behavior after changes

- After changing `shortcuts.vdf`, the app tries to restart Steam automatically.
- Automatic restart is performed only if no other game is currently running in Steam.
- If this exact `non-Steam` game is the one currently running, Steam will still be restarted.
- If the app is currently running outside Steam, only Steam is restarted and the app itself keeps running.
- If the app is currently running through Steam, the current instance closes, Steam restarts, and then the app is launched again through `steam://rungameid/...`.
- If you remove and add the `non-Steam` entry in Steam again, the `rungameid` identifier may change.
- In that case, the old desktop shortcut will need to be recreated.

## Single-instance behavior

The app should no longer leave multiple tray icons behind.

The rules are:

- if a normal instance is already running and a new normal instance starts, the new one takes priority and the old one exits;
- if a normal instance is already running and a Steam-launched instance starts, the new Steam-launched instance takes priority and the old one exits;
- if a Steam-launched instance is already running and a normal instance starts, the current Steam-launched instance is kept and the new launch exits;
- if a Steam-launched instance is already running and a new Steam-launched instance starts, the new Steam-launched instance takes priority.

Because of that, the process that was actually launched by Steam stays preferred, which is the one that should hold the `non-Steam game` status.

## Limitations

- The app only works with the `non-Steam` entry whose `Exe` matches the path to the current running `exe`.
- If Steam contains a different path, for example a Debug build or a copy of the file in another folder, the app will not find the correct entry.
- Starting the `exe` directly outside Steam does not give a `non-Steam game` status.
- To get the status, launch either from the Steam library or through the created `steam://rungameid/...` shortcut.
- At the moment, a `.url` shortcut is created. That is enough to launch through Steam.
- Autostart with Windows is not implemented yet.
- A fully custom WPF context menu instead of `ContextMenuStrip` is not implemented yet.

## Current stack

- `.NET 10`
- `WPF`
- `Windows Forms NotifyIcon` for tray integration

## Current implementation structure

- [App.xaml](C:\Vivoderin\SteamGameCustomStatus\App.xaml) - app definition without `StartupUri`
- [App.xaml.cs](C:\Vivoderin\SteamGameCustomStatus\App.xaml.cs) - tray icon, context menu, dynamic tray action visibility, and app startup
- [MainWindow.xaml](C:\Vivoderin\SteamGameCustomStatus\MainWindow.xaml) - minimal control window with Steam registration status
- [MainWindow.xaml.cs](C:\Vivoderin\SteamGameCustomStatus\MainWindow.xaml.cs) - window hiding logic, status refresh, and available-action switching
- [RenameShortcutWorkflow.cs](C:\Vivoderin\SteamGameCustomStatus\RenameShortcutWorkflow.cs) - rename workflow
- [SteamRestartWorkflow.cs](C:\Vivoderin\SteamGameCustomStatus\SteamRestartWorkflow.cs) - workflow for safely applying the new name by stopping and relaunching Steam
- [DesktopShortcutWorkflow.cs](C:\Vivoderin\SteamGameCustomStatus\DesktopShortcutWorkflow.cs) - desktop shortcut workflow
- [OpenSteamAddGameWorkflow.cs](C:\Vivoderin\SteamGameCustomStatus\OpenSteamAddGameWorkflow.cs) - workflow for opening Steam to add a `non-Steam game`
- [SteamShortcutRenamer.cs](C:\Vivoderin\SteamGameCustomStatus\SteamShortcutRenamer.cs) - reading, searching, changing, and writing `shortcuts.vdf`
- [SteamGameCustomStatus.csproj](C:\Vivoderin\SteamGameCustomStatus\SteamGameCustomStatus.csproj) - WPF, icon, and single-file publish settings
- [Icon.ico](C:\Vivoderin\SteamGameCustomStatus\Icon.ico) - app icon

## Build

Regular build:

```powershell
dotnet build
```

Publish into a single `exe`:

```powershell
dotnet publish -c Release
```

Verified publish result:

- `Release`
- `win-x64`
- `single-file`
- `self-contained`

## Where the built exe is located

After `publish`, the final file is located at:

```text
C:\Vivoderin\SteamGameCustomStatus\bin\Release\net10.0-windows\win-x64\publish\SteamGameCustomStatus.exe
```

The current `publish` also creates a debug file:

```text
C:\Vivoderin\SteamGameCustomStatus\bin\Release\net10.0-windows\win-x64\publish\SteamGameCustomStatus.pdb
```

## Why the exe is large

Right now the app is published as:

- `single-file`
- `self-contained`
- for `win-x64`

Because of that, the single output file contains not only the app code but also the .NET runtime. For WPF, that results in a large final `exe` size.

## Next logical steps

- Add explicit selection of the found `non-Steam` entry if multiple matches exist.
- Add display of the current `AppName` and `rungameid` directly in the UI.
- Add `.lnk` creation as an alternative to `.url`.
- Add settings and persistence for user state.
- Add Windows autostart.
