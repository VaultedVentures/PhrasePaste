# PhrasePaste

Systray utility that pastes phrases into any application via global hotkeys.
Boots with Windows. Same C# / .NET 9 WinForms toolchain and patterns as Clippa
(RegisterHotKey on a hidden window, registry Run-key autostart, clipboard +
simulated Ctrl+V with clipboard restore).

## Using it

- `Win+Alt+P` (default) - open the searchable phrase picker near the cursor.
  Rows show the phrase name, a preview of the text that will be pasted, and the
  hotkey. Type to filter (substring or initials), Up/Down to move, Enter or a
  single click to paste, Esc to close. Right-click a row for paste / copy /
  edit / delete, Ctrl+N for a new phrase, F2 or Ctrl+E to edit the selected one.
- Per-phrase hotkeys - paste immediately into the focused app.
- Tray icon, left-click - open the picker. Right-click - the tray menu:
  Pick a phrase, Save selection as phrase, Edit phrases..., Start with Windows,
  Edit phrases.yaml, Quit.

## Managing phrases

Right-click the tray icon, then **Edit phrases...**. No YAML, no reload step:

- The list on the left is every phrase; the fields on the right are the one
  you have selected.
- **Hotkeys are captured, not typed.** Click the Hotkey box and press the keys
  you want; Delete clears it. The box only ever holds a combination the app can
  actually register, and it tells you straight away when something is already
  taken (by another phrase, or by another app).
- New / Duplicate / Delete / Move up / Move down for the list.
- Save (or Ctrl+S) writes `phrases.yaml` and applies the hotkeys immediately -
  hotkeys are re-registered live, so there is never a restart or a reload step.
- "Edit raw file" opens `phrases.yaml` in your editor of choice if you prefer.

### Save selection as phrase

Copy text in any app, then tray menu -> **Save selection as phrase** (or set a
global hotkey for it in the editor, e.g. `Win+Alt+S`). It copies the current
selection, opens the editor with the text already filled in and a name taken
from the first line, and all you do is press a hotkey and Save. Nothing needs
typing twice.

## Configuration

`%APPDATA%\PhrasePaste\phrases.yaml` - written by the editor, hand-editable.

```yaml
popup_hotkey: "Win+Alt+P"
capture_hotkey: ""

phrases:
  - name: "Scott"
    hotkey: "Ctrl+Shift+r"
    text: "Scott "
  - name: "Sign-off"
    hotkey: "Ctrl+Shift+Alt+k"
    text: |
      Kind regards,

      Scott Phillips
```

Hotkey syntax: `Ctrl` / `Alt` / `Shift` / `Win` joined with `+`, then a key
(letters, digits, F1-F24, Space, Enter, Tab, Backspace, Delete, arrows,
punctuation names, ...). A phrase without a hotkey is still reachable from the
picker and the tray menu.

**The file is reloaded automatically.** Save it in your editor and the app picks
the change up within about a second and re-registers the hotkeys; there is no
"Reload" command. A `.bak` of the previous contents is kept alongside it on
every write. Saving from the editor rewrites the file (comments inside the
phrase list are not preserved, the header is); use "Edit raw file" if you want
to keep your own comments permanently.

Hotkeys that are already in use by another app are skipped and reported; the
details are in `%APPDATA%\PhrasePaste\startup.log`.

## Paste mechanics

Copies the phrase to the clipboard, sends Ctrl+V to the focused window, then
restores your previous clipboard after ~800 ms. If the clipboard was empty, the
phrase is left there so you can paste it again. Note: pasting into an elevated
(admin) window from a non-elevated process is blocked by Windows (UIPI) - same
limitation as every paste utility.

## Files

- `Program.cs` - entry point, single-instance mutex, `--selftest`
- `PhrasePasteApp.cs` - tray icon, hotkey dispatch, paste engine, autostart,
  config watching
- `PhraseEditor.cs` - the phrase manager window + the hotkey capture box
- `PhrasePicker.cs` - the searchable picker window
- `PhraseStore.cs` - reads and writes `phrases.yaml` (block scalars, backups)
- `Phrases.cs` - the config model + the first-run sample
- `Theme.cs` - the shared dark palette, flat buttons, dark title bars
- `HotkeyManager.cs` / `HotkeyWindow.cs` - hotkey parsing, formatting,
  availability probing, registration
- `SelfTest.cs` - YAML round-trip checks (`PhrasePaste.exe --selftest [outfile]`)
- Logs: `%APPDATA%\PhrasePaste\startup.log`

## Build & deploy

```bash
export PATH="/c/Program Files/dotnet:$PATH"
cd /c/Users/scott/projects/PhrasePaste
dotnet build
./bin/Debug/net9.0-windows/PhrasePaste.exe --selftest   # YAML round-trip: expect RESULT: PASS
# deploy = copy the WHOLE bin output (dependency DLLs):
powershell -Command "Stop-Process -Name PhrasePaste -Force -ErrorAction SilentlyContinue; Start-Sleep 1"
cp -r bin/Debug/net9.0-windows/. "$LOCALAPPDATA/Programs/PhrasePaste/"
powershell -Command "Start-Process -FilePath \"$env:LOCALAPPDATA\Programs\PhrasePaste\PhrasePaste.exe\""
```

Autostart is a single mechanism only: registry `HKCU\...\Run` value
`PhrasePaste`, synced at startup and toggled from the tray menu ("Start with
Windows"). No Startup-folder shortcut (double-launch hazard, same rule as
Clippa v2).
