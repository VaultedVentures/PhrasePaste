# PhrasePaste

Systray utility that pastes phrases into any application via global hotkeys.
Boots with Windows. Same C# / .NET 9 WinForms toolchain and patterns as Clippa
(RegisterHotKey on a hidden window, registry Run-key autostart, clipboard +
simulated Ctrl+V with clipboard restore).

## Hotkeys

- `Win+Alt+P` (default) - open the searchable phrase picker near the cursor.
  Type to filter, Up/Down to move, Enter to paste, Esc to close.
- Per-phrase hotkeys from the config, e.g. `Ctrl+Alt+1` - paste immediately
  into the focused app.
- Left-click the tray icon - open the picker. Right-click - tray menu.

## Configuration

`%APPDATA%\PhrasePaste\phrases.yaml` (created with a sample on first run).
Edit it and use tray menu -> Reload phrases; no restart needed.

```yaml
popup_hotkey: "Win+Alt+P"

phrases:
  - name: "Email signature"
    text: |
      Regards,
      Scott
    hotkey: "Ctrl+Alt+1"
```

Hotkey syntax: `Ctrl` / `Alt` / `Shift` / `Win` joined with `+`, then a key
(letters, digits, F1-F24, Space, Enter, Tab, Backspace, Delete, arrows,
punctuation names, ...). A phrase without a `hotkey` is still reachable from
the picker and tray menu. Hotkeys that are already in use by another app are
skipped with a tray balloon warning (details in `startup.log`).

## Paste mechanics

Copies the phrase to the clipboard, sends Ctrl+V to the focused window, then
restores your previous clipboard after ~800 ms. If the clipboard was empty,
the phrase is left there so you can paste it again. Note: pasting into an
elevated (admin) window from a non-elevated process is blocked by Windows
(UIPI) - same limitation as every paste utility.

## Files

- `Program.cs` - entry point, single-instance mutex
- `PhrasePasteApp.cs` - tray icon, hotkey dispatch, paste engine, autostart
- `HotkeyManager.cs` / `HotkeyWindow.cs` - hotkey parsing + registration
- `Phrases.cs` - YAML model + sample config
- `PhrasePicker.cs` - searchable picker window
- Logs: `%APPDATA%\PhrasePaste\startup.log`

## Build & deploy

```bash
export PATH="/c/Program Files/dotnet:$PATH"
cd /c/Users/scott/projects/PhrasePaste
dotnet build
# deploy = copy the WHOLE bin output (dependency DLLs):
powershell -Command "Stop-Process -Name PhrasePaste -Force -ErrorAction SilentlyContinue; Start-Sleep 1"
cp -r bin/Debug/net9.0-windows/. "$LOCALAPPDATA/Programs/PhrasePaste/"
"$LOCALAPPDATA/Programs/PhrasePaste/PhrasePaste.exe"
```

Autostart is a single mechanism only: registry `HKCU\...\Run` value
`PhrasePaste`, synced at startup and toggled from the tray menu ("Start with
Windows"). No Startup-folder shortcut (double-launch hazard, same rule as
Clippa v2).
