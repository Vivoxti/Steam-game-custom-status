# Steam Game Custom Status

Minimal tray-first Windows app for launching as a `non-Steam game` and controlling how Steam shows your running status.

## What it does

- runs quietly from the system tray
- detects whether the current published `exe` is already added to Steam
- renames the matching non-Steam entry in `shortcuts.vdf`
- creates desktop shortcuts that launch through `steam://rungameid/...`
- opens Steam to the add-game flow when the app is not yet registered
- keeps a single active instance and prefers the Steam-launched instance when needed
- can restart Steam after rename so the updated title is applied

## Stack

- `.NET 10`
- `WPF`
- `Windows Forms NotifyIcon`

## Publish

Canonical executable build:

```powershell
dotnet publish -c Release
```

Published executable (relative to repository root):

```text
bin\Release\net10.0-windows\win-x64\publish\SteamGameCustomStatus.exe
```

