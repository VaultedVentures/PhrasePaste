using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows.Forms;
using Microsoft.Win32;

namespace PhrasePaste;

/// <summary>
/// PhrasePaste tray application: global hotkeys paste phrases into whatever
/// app has focus. Config lives in %APPDATA%\PhrasePaste\phrases.yaml, which the
/// built-in editor owns so nobody has to hand-write YAML to add a phrase.
/// </summary>
internal sealed class PhrasePasteApp : ApplicationContext
{
    private const int PopupHotkeyId = 0x4000;
    private const int CaptureHotkeyId = 0x4001;
    private const int FirstPhraseHotkeyId = 0x4100;
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValueName = "PhrasePaste";
    private const string AppName = "PhrasePaste";

    private readonly NotifyIcon _tray;
    private readonly HotkeyWindow _hotkeyWindow;
    private readonly string _dataDir;
    private readonly string _configPath;
    private readonly string _settingsPath;
    private readonly string _logPath;

    private readonly ContextMenuStrip _menu;
    private readonly ToolStripMenuItem _pickItem;
    private readonly ToolStripMenuItem _captureItem;
    private readonly ToolStripMenuItem _editItem;
    private readonly ToolStripMenuItem _autostartItem;
    private readonly System.Windows.Forms.Timer _reloadTimer;

    private PhraseConfig _config = new();
    private readonly List<(int Id, string Display)> _registeredHotkeys = new();
    private readonly Dictionary<int, uint> _hotkeyVk = new();

    /// <summary>
    /// Combinations this process currently owns. The editor needs them: a probe
    /// for a hotkey we already hold always fails, and without this every phrase
    /// that already works would look like a clash with another app.
    /// </summary>
    private readonly List<HotkeyCombo> _liveCombos = new();
    private bool _autostartEnabled;
    private IntPtr _menuTarget;
    private PhrasePicker? _openPicker;
    private IntPtr _pickerTarget;

    /// <summary>Exact file contents we last read or wrote, so our own saves never look like an edit.</summary>
    private string _lastKnownText = "";
    private DateTime _lastWriteUtc;
    private int _modalDepth;

    public PhrasePasteApp()
    {
        _dataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), AppName);
        Directory.CreateDirectory(_dataDir);
        _configPath = Path.Combine(_dataDir, "phrases.yaml");
        _settingsPath = Path.Combine(_dataDir, "settings.json");
        _logPath = Path.Combine(_dataDir, "startup.log");

        Log($"--- {AppName} starting ---");

        AppDomain.CurrentDomain.UnhandledException += (_, e) => Log($"UNHANDLED: {e.ExceptionObject}");
        Application.ThreadException += (_, e) => Log($"THREAD EXCEPTION: {e.Exception}");

        _hotkeyWindow = new HotkeyWindow();
        _hotkeyWindow.HotKeyPressed += OnHotKeyPressed;
        _hotkeyWindow.CreateHandle(new CreateParams { Caption = "PhrasePasteHotkeyWindow" });

        _autostartEnabled = ReadAutoStartPreference();
        if (!File.Exists(_settingsPath))
        {
            WriteAutoStartPreference(_autostartEnabled);
        }

        _tray = new NotifyIcon
        {
            Icon = TrayIcon.Create(),
            Text = "PhrasePaste - paste phrases",
            Visible = true,
        };
        _tray.MouseClick += OnTrayMouseClick;

        // Built once and updated in place: swapping ContextMenuStrip while a
        // menu is open is a crash, and the text only ever changes.
        _menu = new ContextMenuStrip();
        Theme.MakeDark(_menu);
        _pickItem = new ToolStripMenuItem("Pick a phrase", null, (_, _) => OpenPicker(_menuTarget));
        _captureItem = new ToolStripMenuItem("Save selection as phrase", null, (_, _) => SaveSelectionAsPhrase(_menuTarget));
        _editItem = new ToolStripMenuItem("Edit phrases\u2026", null, (_, _) => OpenEditor());
        _autostartItem = new ToolStripMenuItem("Start with Windows", null, (_, _) => ToggleAutoStart());
        _menu.Items.Add(_pickItem);
        _menu.Items.Add(_captureItem);
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add(_editItem);
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add(_autostartItem);
        _menu.Items.Add(new ToolStripMenuItem("Edit phrases.yaml", null, (_, _) => OpenRawConfig()));
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add(new ToolStripMenuItem("Quit", null, (_, _) => Quit()));
        _menu.Opening += (_, _) =>
        {
            _menuTarget = GetForegroundWindow();
            RefreshMenu();
        };
        _tray.ContextMenuStrip = _menu;

        LoadConfig();

        // Requirement: boots with Windows. Make the registry Run key match the toggle state.
        SyncAutoStart();
        RefreshMenu();

        // Hand edits to phrases.yaml land without the user pressing anything.
        _reloadTimer = new System.Windows.Forms.Timer { Interval = 800 };
        _reloadTimer.Tick += (_, _) =>
        {
            if (_modalDepth == 0) TryExternalReload();
        };
        _reloadTimer.Start();

        Log("startup complete");
    }

    // ---------------------------------------------------------------- hotkeys

    private void OnHotKeyPressed(int id)
    {
        try
        {
            if (id == PopupHotkeyId)
            {
                OpenPicker(GetForegroundWindow());
                return;
            }

            if (id == CaptureHotkeyId)
            {
                SaveSelectionAsPhrase(GetForegroundWindow());
                return;
            }

            var phrase = GetPhraseForId(id);
            if (phrase is null) return;

            if (_openPicker is not null)
            {
                // A phrase hotkey while the picker is open = shortcut paste into the
                // app the user came from.
                var target = _pickerTarget;
                _openPicker.Close();
                PasteTextIntoForeground(phrase.Text, target);
                return;
            }

            _hotkeyVk.TryGetValue(id, out var vk);
            PasteTextIntoForeground(phrase.Text, IntPtr.Zero, vk);
        }
        catch (Exception ex)
        {
            Log($"hotkey handler error: {ex}");
        }
    }

    private Phrase? GetPhraseForId(int id)
    {
        var idx = id - FirstPhraseHotkeyId;
        return idx >= 0 && idx < _config.Phrases.Count ? _config.Phrases[idx] : null;
    }

    // ---------------------------------------------------------------- picker

    private void OnTrayMouseClick(object? sender, MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Left)
        {
            OpenPicker(GetForegroundWindow());
        }
    }

    private void OpenPicker(IntPtr target)
    {
        if (_config.Phrases.Count == 0)
        {
            // An empty picker is a dead end; the editor is the thing that helps.
            ShowBalloon(AppName, "No phrases yet - add one below.", ToolTipIcon.Info);
            OpenEditor();
            return;
        }

        if (_openPicker is not null)
        {
            _openPicker.FocusSearch();
            return;
        }

        var picker = new PhrasePicker(_config.Phrases);
        _openPicker = picker;
        _pickerTarget = target;
        try
        {
            ShowModal(() => picker.ShowDialog());
        }
        finally
        {
            _openPicker = null;
        }

        var action = picker.Action;
        var phrase = picker.Target;
        picker.Dispose();

        switch (action)
        {
            case PickerAction.Paste:
                if (phrase is not null) PasteTextIntoForeground(phrase.Text, _pickerTarget);
                break;

            case PickerAction.Copy:
                if (phrase is not null) CopyTextToClipboard(phrase.Text, _pickerTarget);
                break;

            case PickerAction.Edit:
                if (phrase is not null) OpenEditor(select: phrase);
                break;

            case PickerAction.New:
                OpenEditor(seed: new Phrase { Name = "New phrase" });
                break;

            case PickerAction.Delete:
                if (phrase is not null) DeletePhrase(phrase);
                break;
        }
    }

    private void DeletePhrase(Phrase phrase)
    {
        var answer = MessageBox.Show(
            $"Delete \"{phrase.DisplayName()}\"?",
            AppName,
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Warning,
            MessageBoxDefaultButton.Button2);

        if (answer != DialogResult.Yes) return;

        var index = _config.Phrases.IndexOf(phrase);
        if (index < 0) return;

        _config.Phrases.RemoveAt(index);
        var error = TrySaveConfig(_config);
        if (error is not null)
        {
            ShowBalloon("Couldn't delete", error, ToolTipIcon.Warning);
            LoadConfig();
            return;
        }

        ApplyConfig();
        RefreshMenu();
        ShowBalloon(AppName, $"Deleted. {_config.Phrases.Count} phrases left.", ToolTipIcon.Info);
    }

    // ---------------------------------------------------------------- editor

    /// <summary>
    /// The phrase manager. <paramref name="seed"/> arrives as an unsaved new
    /// phrase (that is how "save selection as phrase" works), so cancelling
    /// the window leaves no trace.
    /// </summary>
    private void OpenEditor(Phrase? seed = null, Phrase? select = null)
    {
        var working = _config.Clone();
        Phrase? preselect = null;

        if (seed is not null)
        {
            working.Phrases.Add(seed);
            preselect = seed;
        }
        else if (select is not null)
        {
            var index = _config.Phrases.IndexOf(select);
            if (index >= 0 && index < working.Phrases.Count) preselect = working.Phrases[index];
        }

        using var editor = new PhraseEditor(working, preselect, _configPath, TrySaveConfig, _liveCombos);
        ShowModal(() => editor.ShowDialog());

        if (!editor.Saved) return;

        _config = editor.Config;
        ApplyConfig();
        RefreshMenu();
        ShowBalloon(AppName, $"{_config.Phrases.Count} phrases saved.", ToolTipIcon.Info);
    }

    /// <summary>
    /// Copy the current selection out of the focused app and open the editor
    /// with it already filled in, so a phrase never has to be typed twice.
    /// </summary>
    private void SaveSelectionAsPhrase(IntPtr target)
    {
        var hwnd = target == IntPtr.Zero ? GetForegroundWindow() : target;
        if (hwnd == IntPtr.Zero)
        {
            ShowBalloon("Nothing to save", "Couldn't tell which window to copy from.", ToolTipIcon.Warning);
            return;
        }

        RestoreForeground(hwnd);
        Thread.Sleep(250);   // let the tray menu close and the target take focus

        try
        {
            SendKeys.SendWait("^c");
        }
        catch (Exception ex)
        {
            Log($"capture: SendKeys failed: {ex.Message}");
            ShowBalloon("Couldn't copy", "Windows blocked the copy from that window.", ToolTipIcon.Warning);
            return;
        }

        Thread.Sleep(200);
        WaitClipboardUnlocked();
        var text = TryGetClipboardText()?.TrimEnd('\r', '\n');

        if (string.IsNullOrWhiteSpace(text))
        {
            ShowBalloon("Nothing to save", "Select some text first, then run this again.", ToolTipIcon.Warning);
            return;
        }

        Log($"capture: grabbed {text.Length} chars from the selection");
        OpenEditor(seed: new Phrase { Name = FirstLine(text), Text = text, Hotkey = "" });
    }

    private static string FirstLine(string text)
    {
        var line = text.Replace("\r", "").Split('\n')
            .FirstOrDefault(l => !string.IsNullOrWhiteSpace(l))?.Trim() ?? "";

        if (line.Length == 0) return "New phrase";
        return line.Length > 40 ? line[..40] : line;
    }

    // ---------------------------------------------------------------- paste

    private void PasteTextIntoForeground(string text, IntPtr target, uint triggeredVk = 0)
    {
        // Normalize line endings: Windows apps expect CRLF in clipboard text.
        text = text.Replace("\r\n", "\n").Replace("\r", "\n").Replace("\n", "\r\n");

        var hwnd = target == IntPtr.Zero ? GetForegroundWindow() : target;
        if (hwnd != IntPtr.Zero)
        {
            RestoreForeground(hwnd);
        }

        var saved = CaptureClipboard();
        if (!SetClipboardText(text))
        {
            Log("paste failed: clipboard busy after retries");
            ShowBalloon("Couldn't paste", "The clipboard was busy; try again.", ToolTipIcon.Warning);
            return;
        }

        // Clipdiary/Clippa also sit on the clipboard. Wait until nobody holds
        // it open, otherwise Ctrl+V lands on a locked/empty clipboard and the
        // user thinks PhrasePaste is dead.
        WaitClipboardUnlocked();

        // WM_HOTKEY lands while the user is still holding the hotkey down, so a
        // synthetic Ctrl+V would reach the target app as Ctrl+Shift+Alt+V - not a
        // paste in any app, and with Alt down it usually trips a menu accelerator
        // instead. Two-modifier combos only worked by luck (Ctrl+Shift+V happens
        // to be paste in most apps). Let the user's keys come up first.
        var keysReleased = WaitForKeysReleased(triggeredVk);

        try
        {
            SendKeys.SendWait("^v");
            Log(keysReleased
                ? $"paste sent ({text.Length} chars)"
                : $"paste sent ({text.Length} chars) - keys still held after wait; paste may not land");
        }
        catch (Exception ex)
        {
            Log($"SendKeys failed: {ex}");
        }

        ScheduleClipboardRestore(saved);
    }

    /// <summary>Put the phrase on the clipboard without pressing Ctrl+V anywhere.</summary>
    private void CopyTextToClipboard(string text, IntPtr target)
    {
        text = text.Replace("\r\n", "\n").Replace("\r", "\n").Replace("\n", "\r\n");

        var hwnd = target == IntPtr.Zero ? GetForegroundWindow() : target;
        if (hwnd != IntPtr.Zero)
        {
            RestoreForeground(hwnd);
        }

        if (!SetClipboardText(text))
        {
            Log("copy failed: clipboard busy after retries");
            ShowBalloon("Couldn't copy", "The clipboard was busy; try again.", ToolTipIcon.Warning);
            return;
        }

        Log($"phrase copied to clipboard ({text.Length} chars)");
    }

    private static IDataObject? CaptureClipboard()
    {
        try
        {
            return Clipboard.GetDataObject();
        }
        catch
        {
            return null;
        }
    }

    private static bool SetClipboardText(string text)
    {
        for (var attempt = 0; attempt < 8; attempt++)
        {
            try
            {
                Clipboard.SetText(text);
                return true;
            }
            catch (ExternalException)
            {
                Thread.Sleep(80);
            }
        }
        return false;
    }

    private static string? TryGetClipboardText()
    {
        for (var attempt = 0; attempt < 10; attempt++)
        {
            try
            {
                return Clipboard.ContainsText() ? Clipboard.GetText() : null;
            }
            catch (ExternalException)
            {
                Thread.Sleep(60);
            }
        }
        return null;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool OpenClipboard(IntPtr hWndNewOwner);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool CloseClipboard();

    /// <summary>
    /// Probe until the clipboard is not exclusively locked (Clipdiary opens
    /// it ~200ms after every copy). Must run on the UI thread.
    /// </summary>
    private static void WaitClipboardUnlocked()
    {
        for (var i = 0; i < 12; i++)
        {
            if (OpenClipboard(IntPtr.Zero))
            {
                CloseClipboard();
                return;
            }
            Thread.Sleep(40);
        }
    }

    private const int VkShift = 0x10;
    private const int VkControl = 0x11;
    private const int VkMenu = 0x12;
    private const int VkLWin = 0x5B;
    private const int VkRWin = 0x5C;

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    /// <summary>
    /// Wait for the user to let go of the modifiers and the key that fired the
    /// hotkey. GetAsyncKeyState reads the OS keyboard state directly, so this
    /// works even though we are blocking our own message pump. Returns true when
    /// every key came up inside the timeout; false means we pasted anyway.
    /// </summary>
    private static bool WaitForKeysReleased(uint triggeredVk)
    {
        var keys = new List<int> { VkControl, VkShift, VkMenu, VkLWin, VkRWin };
        if (triggeredVk != 0) keys.Add((int)triggeredVk);

        for (var i = 0; i < 50; i++)   // up to ~750ms
        {
            var anyDown = false;
            foreach (var vk in keys)
            {
                if ((GetAsyncKeyState(vk) & 0x8000) != 0)
                {
                    anyDown = true;
                    break;
                }
            }
            if (!anyDown) return true;
            Thread.Sleep(15);
        }
        return false;
    }

    /// <summary>
    /// Restore the clipboard contents that existed before the paste, shortly
    /// after the target app has had a chance to read them. If the clipboard
    /// was empty, leave the phrase there (handy for pasting again).
    /// </summary>
    private void ScheduleClipboardRestore(IDataObject? saved)
    {
        if (saved is null) return;

        var timer = new System.Windows.Forms.Timer { Interval = 900 };
        var attempts = 0;
        timer.Tick += (_, _) =>
        {
            attempts++;
            try
            {
                Clipboard.SetDataObject(saved, true);
                timer.Stop();
                timer.Dispose();
            }
            catch (Exception ex)
            {
                if (attempts >= 6)
                {
                    timer.Stop();
                    timer.Dispose();
                    Log($"clipboard restore failed: {ex.Message}");
                }
                else
                {
                    timer.Interval = 150;
                }
            }
        };
        timer.Start();
    }

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("user32.dll")]
    private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);

    /// <summary>
    /// Bring a captured window back to the foreground. AttachThreadInput is
    /// the clipboard-manager trick that bypasses the foreground-lock without
    /// flashing the menu bar (the Alt-key hack would).
    /// </summary>
    private static void RestoreForeground(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return;

        var currentThread = GetCurrentThreadId();
        var targetThread = GetWindowThreadProcessId(hwnd, out _);
        var attached = false;
        if (targetThread != 0 && targetThread != currentThread)
        {
            attached = AttachThreadInput(currentThread, targetThread, true);
        }

        try
        {
            SetForegroundWindow(hwnd);
        }
        finally
        {
            if (attached)
            {
                AttachThreadInput(currentThread, targetThread, false);
            }
        }
    }

    // ---------------------------------------------------------------- config

    private void LoadConfig()
    {
        try
        {
            if (!File.Exists(_configPath))
            {
                File.WriteAllText(_configPath, Phrases.SampleConfigText());
                Log($"created sample config at {_configPath}");
            }

            var text = File.ReadAllText(_configPath);
            _config = PhraseStore.Parse(text);
            _lastKnownText = text;
            _lastWriteUtc = File.GetLastWriteTimeUtc(_configPath);
            Log($"config loaded: {_config.Phrases.Count} phrases, picker {_config.PopupHotkey}");
        }
        catch (Exception ex)
        {
            Log($"config load failed: {ex.Message}");
            ShowBalloon("PhrasePaste: config error",
                $"Couldn't read phrases.yaml: {ex.Message}\nKeeping the previous phrase list.",
                ToolTipIcon.Warning);
        }

        ApplyConfig();
    }

    /// <summary>Write the config, or explain why it could not be written.</summary>
    private string? TrySaveConfig(PhraseConfig config)
    {
        try
        {
            PhraseStore.Save(_configPath, config);
            _lastKnownText = PhraseStore.Serialize(config);
            _lastWriteUtc = File.GetLastWriteTimeUtc(_configPath);
            Log($"config saved: {config.Phrases.Count} phrases");
            return null;
        }
        catch (Exception ex)
        {
            Log($"config save failed: {ex.Message}");
            return $"Couldn't write {_configPath}\n\n{ex.Message}";
        }
    }

    /// <summary>
    /// Pick up edits made outside the app (the editor's "Edit raw file", or
    /// Notepad) without the user pressing Reload. Skipped while a dialog is
    /// open so the open window's phrase list cannot be swapped underneath it.
    /// </summary>
    private void TryExternalReload()
    {
        try
        {
            if (!File.Exists(_configPath)) return;
            if (File.GetLastWriteTimeUtc(_configPath) == _lastWriteUtc) return;

            var text = File.ReadAllText(_configPath);
            if (text == _lastKnownText) return;

            _lastKnownText = text;
            _lastWriteUtc = File.GetLastWriteTimeUtc(_configPath);

            LoadConfig();
            RefreshMenu();
            ShowBalloon(AppName, $"Phrases reloaded from the file ({_config.Phrases.Count}).", ToolTipIcon.Info);
        }
        catch (Exception ex)
        {
            Log($"external reload failed: {ex.Message}");
        }
    }

    private void ApplyConfig()
    {
        foreach (var (id, _) in _registeredHotkeys)
        {
            HotkeyManager.UnregisterHotKey(_hotkeyWindow.Handle, id);
        }
        _registeredHotkeys.Clear();
        _hotkeyVk.Clear();
        _liveCombos.Clear();

        var warnings = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (HotkeyManager.TryParse(_config.PopupHotkey, out var popup, out var popupError))
        {
            if (HotkeyManager.RegisterHotKey(_hotkeyWindow.Handle, PopupHotkeyId, popup.Modifiers, popup.Vk))
            {
                _registeredHotkeys.Add((PopupHotkeyId, popup.Display));
                _liveCombos.Add(popup);
                seen.Add(CanonicalKey(popup));
                Log($"popup hotkey OK: {popup.Display}");
            }
            else
            {
                var msg = $"popup hotkey {popup.Display} is already in use (Win32 error {Marshal.GetLastWin32Error()})";
                warnings.Add(msg);
                Log(msg);
            }
        }
        else if (!string.IsNullOrWhiteSpace(_config.PopupHotkey))
        {
            warnings.Add($"popup hotkey invalid: {popupError}");
            Log($"popup hotkey invalid: {popupError}");
        }

        if (!string.IsNullOrWhiteSpace(_config.CaptureHotkey))
        {
            if (HotkeyManager.TryParse(_config.CaptureHotkey, out var capture, out var captureError))
            {
                if (seen.Add(CanonicalKey(capture))
                    && HotkeyManager.RegisterHotKey(_hotkeyWindow.Handle, CaptureHotkeyId, capture.Modifiers, capture.Vk))
                {
                    _registeredHotkeys.Add((CaptureHotkeyId, capture.Display));
                    _liveCombos.Add(capture);
                    Log($"capture hotkey OK: {capture.Display}");
                }
                else
                {
                    var msg = $"capture hotkey {capture.Display} is already in use";
                    warnings.Add(msg);
                    Log(msg);
                }
            }
            else
            {
                warnings.Add($"capture hotkey invalid: {captureError}");
                Log($"capture hotkey invalid: {captureError}");
            }
        }

        for (var i = 0; i < _config.Phrases.Count; i++)
        {
            var phrase = _config.Phrases[i];
            if (phrase is null || string.IsNullOrWhiteSpace(phrase.Hotkey)) continue;

            if (!HotkeyManager.TryParse(phrase.Hotkey, out var combo, out var err))
            {
                warnings.Add($"invalid hotkey on '{phrase.DisplayName()}': {phrase.Hotkey} ({err})");
                continue;
            }

            if (!seen.Add(CanonicalKey(combo)))
            {
                warnings.Add($"duplicate hotkey on '{phrase.DisplayName()}': {phrase.Hotkey}");
                continue;
            }

            var id = FirstPhraseHotkeyId + i;
            if (HotkeyManager.RegisterHotKey(_hotkeyWindow.Handle, id, combo.Modifiers, combo.Vk))
            {
                _registeredHotkeys.Add((id, combo.Display));
                _hotkeyVk[id] = combo.Vk;
                _liveCombos.Add(combo);
                Log($"phrase hotkey OK [{phrase.DisplayName()}]: {combo.Display}");
            }
            else
            {
                var msg = $"hotkey for '{phrase.DisplayName()}' ({combo.Display}) is already in use (Win32 error {Marshal.GetLastWin32Error()})";
                warnings.Add(msg);
                Log(msg);
            }
        }

        if (warnings.Count > 0)
        {
            var body = string.Join("\n", warnings.Take(3));
            if (warnings.Count > 3) body += $"\n(+{warnings.Count - 3} more - see {Path.GetFileName(_logPath)})";
            ShowBalloon("PhrasePaste: hotkey warnings", body, ToolTipIcon.Warning);
        }
    }

    private static string CanonicalKey(HotkeyCombo combo) => $"{combo.Modifiers}:{combo.Vk}";

    // ---------------------------------------------------------------- tray menu

    private void RefreshMenu()
    {
        _pickItem.Text = $"Pick a phrase   ({_config.PopupHotkey})";
        _captureItem.Text = string.IsNullOrWhiteSpace(_config.CaptureHotkey)
            ? "Save selection as phrase"
            : $"Save selection as phrase   ({_config.CaptureHotkey})";
        _editItem.Text = $"Edit phrases\u2026   ({_config.Phrases.Count})";
        _autostartItem.Checked = _autostartEnabled;

        var text = $"{AppName} - {_config.Phrases.Count} phrases";
        _tray.Text = text.Length > 62 ? text[..62] : text;
    }

    private void ToggleAutoStart()
    {
        _autostartEnabled = !_autostartEnabled;
        WriteAutoStartPreference(_autostartEnabled);
        SyncAutoStart();
        RefreshMenu();
    }

    private void OpenRawConfig()
    {
        try
        {
            Process.Start(new ProcessStartInfo(_configPath) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log($"open config failed: {ex.Message}");
            ShowBalloon("Couldn't open phrases.yaml", ex.Message, ToolTipIcon.Warning);
        }
    }

    // ---------------------------------------------------------------- autostart

    /// <summary>
    /// Autostart preference lives in settings.json; the registry Run key is
    /// only the mechanism. Default is ON (the app is meant to boot with
    /// Windows), so a missing settings file means enabled.
    /// </summary>
    private bool ReadAutoStartPreference()
    {
        try
        {
            if (File.Exists(_settingsPath))
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(_settingsPath));
                if (doc.RootElement.TryGetProperty("autostart", out var el)
                    && el.ValueKind == JsonValueKind.False)
                {
                    return false;
                }
            }
        }
        catch (Exception ex)
        {
            Log($"read autostart preference failed: {ex.Message}");
        }
        return true;
    }

    private void WriteAutoStartPreference(bool enabled)
    {
        try
        {
            File.WriteAllText(_settingsPath, $"{{\"autostart\": {(enabled ? "true" : "false")}}}\n");
        }
        catch (Exception ex)
        {
            Log($"write autostart preference failed: {ex.Message}");
        }
    }

    private void SyncAutoStart()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true)
                            ?? Registry.CurrentUser.CreateSubKey(RunKeyPath);
            if (key is null)
            {
                Log("autostart sync failed: could not open Run key");
                return;
            }

            var exe = $"\"{Application.ExecutablePath}\"";
            var existing = key.GetValue(RunValueName) as string;

            if (_autostartEnabled)
            {
                if (!string.Equals(existing, exe, StringComparison.OrdinalIgnoreCase))
                {
                    key.SetValue(RunValueName, exe);
                }
            }
            else if (existing is not null)
            {
                key.DeleteValue(RunValueName, throwOnMissingValue: false);
            }

            Log(_autostartEnabled ? $"autostart enabled: {exe}" : "autostart disabled");
        }
        catch (Exception ex)
        {
            Log($"autostart sync failed: {ex.Message}");
        }
    }

    // ---------------------------------------------------------------- misc

    /// <summary>
    /// Run a modal window and, once the last one is gone, pick up any file
    /// edit that arrived while it was open.
    /// </summary>
    private void ShowModal(Action show)
    {
        _modalDepth++;
        try
        {
            show();
        }
        finally
        {
            _modalDepth--;
            if (_modalDepth == 0) TryExternalReload();
        }
    }

    private void Quit()
    {
        try
        {
            _reloadTimer.Stop();
            _reloadTimer.Dispose();
        }
        catch
        {
            // shutting down anyway
        }

        foreach (var (id, _) in _registeredHotkeys)
        {
            HotkeyManager.UnregisterHotKey(_hotkeyWindow.Handle, id);
        }
        _registeredHotkeys.Clear();
        _hotkeyWindow.DestroyHandle();

        _tray.Visible = false;
        _tray.Dispose();

        Log("quitting");
        ExitThread();
    }

    private void ShowBalloon(string title, string text, ToolTipIcon icon)
    {
        try
        {
            _tray.ShowBalloonTip(4000, title, text, icon);
        }
        catch
        {
            // balloons are best-effort; everything important is in the log
        }
    }

    private void Log(string message)
    {
        try
        {
            File.AppendAllText(_logPath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  {message}{Environment.NewLine}");
        }
        catch
        {
            // never let logging take the app down
        }
        Debug.WriteLine($"[{AppName}] {message}");
    }
}

/// <summary>Runtime-drawn tray icon: dark circle with a bold white "P".</summary>
internal static class TrayIcon
{
    public static Icon Create()
    {
        using var bmp = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);

            using var bg = new SolidBrush(Theme.Accent);
            g.FillEllipse(bg, 1, 1, 30, 30);

            TextRenderer.DrawText(g, "P", new Font("Segoe UI", 19f, FontStyle.Bold, GraphicsUnit.Pixel),
                new Rectangle(0, 3, 32, 26), Color.White,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        }
        return Icon.FromHandle(bmp.GetHicon());
    }
}
