using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows.Forms;
using Microsoft.Win32;
using YamlDotNet.Serialization;

namespace PhrasePaste;

/// <summary>
/// PhrasePaste tray application: global hotkeys paste phrases into whatever
/// app has focus. Config lives in %APPDATA%\PhrasePaste\phrases.yaml.
/// </summary>
internal sealed class PhrasePasteApp : ApplicationContext
{
    private const int PopupHotkeyId = 0x4000;
    private const int FirstPhraseHotkeyId = 0x4001;
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValueName = "PhrasePaste";
    private const string AppName = "PhrasePaste";

    private readonly NotifyIcon _tray;
    private readonly HotkeyWindow _hotkeyWindow;
    private readonly string _dataDir;
    private readonly string _configPath;
    private readonly string _settingsPath;
    private readonly string _logPath;

    private PhraseConfig _config = new();
    private readonly List<(int Id, string Display)> _registeredHotkeys = new();
    private bool _autostartEnabled;
    private IntPtr _menuTarget;
    private PhrasePicker? _openPicker;
    private IntPtr _pickerTarget;

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

        LoadConfig();

        // Requirement: boots with Windows. Make the registry Run key match the toggle state.
        SyncAutoStart();
        RebuildMenu();

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

            PasteTextIntoForeground(phrase.Text, IntPtr.Zero);
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
            ShowBalloon("No phrases yet", "Add phrases to phrases.yaml, then choose Reload.", ToolTipIcon.Info);
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
            picker.ShowDialog();
        }
        finally
        {
            _openPicker = null;
        }

        if (picker.Selected is { } phrase && !string.IsNullOrWhiteSpace(phrase.Text))
        {
            PasteTextIntoForeground(phrase.Text, _pickerTarget);
        }
        picker.Dispose();
    }

    // ---------------------------------------------------------------- paste

    private void PasteTextIntoForeground(string text, IntPtr target)
    {
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

        try
        {
            SendKeys.SendWait("^v");
        }
        catch (Exception ex)
        {
            Log($"SendKeys failed: {ex}");
        }

        ScheduleClipboardRestore(saved);
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
        for (var attempt = 0; attempt < 4; attempt++)
        {
            try
            {
                Clipboard.SetText(text);
                return true;
            }
            catch (ExternalException)
            {
                Thread.Sleep(120);
            }
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

        var timer = new System.Windows.Forms.Timer { Interval = 800 };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            timer.Dispose();
            try
            {
                Clipboard.SetDataObject(saved, true);
            }
            catch (Exception ex)
            {
                Log($"clipboard restore failed: {ex.Message}");
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
                File.WriteAllText(_configPath, Phrases.SampleConfigText);
                Log($"created sample config at {_configPath}");
            }

            var text = File.ReadAllText(_configPath);
            var deserializer = new DeserializerBuilder().IgnoreUnmatchedProperties().Build();
            var loaded = deserializer.Deserialize<PhraseConfig>(text) ?? new PhraseConfig();
            loaded.Phrases ??= new List<Phrase>();
            loaded.PopupHotkey = string.IsNullOrWhiteSpace(loaded.PopupHotkey) ? "Win+Alt+P" : loaded.PopupHotkey;
            _config = loaded;
            Log($"config loaded: {_config.Phrases.Count} phrases, popup {_config.PopupHotkey}");
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

    private void ApplyConfig()
    {
        foreach (var (id, _) in _registeredHotkeys)
        {
            HotkeyManager.UnregisterHotKey(_hotkeyWindow.Handle, id);
        }
        _registeredHotkeys.Clear();

        var warnings = new List<string>();

        if (HotkeyManager.TryParse(_config.PopupHotkey, out var popup, out var popupError))
        {
            if (HotkeyManager.RegisterHotKey(_hotkeyWindow.Handle, PopupHotkeyId, popup.Modifiers, popup.Vk))
            {
                _registeredHotkeys.Add((PopupHotkeyId, popup.Display));
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

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < _config.Phrases.Count; i++)
        {
            var phrase = _config.Phrases[i];
            if (phrase is null || string.IsNullOrWhiteSpace(phrase.Hotkey)) continue;

            if (!seen.Add(phrase.Hotkey))
            {
                warnings.Add($"duplicate hotkey on '{phrase.Name}': {phrase.Hotkey}");
                continue;
            }
            if (!HotkeyManager.TryParse(phrase.Hotkey, out var combo, out var err))
            {
                warnings.Add($"invalid hotkey on '{phrase.Name}': {phrase.Hotkey} ({err})");
                continue;
            }

            var id = FirstPhraseHotkeyId + i;
            if (HotkeyManager.RegisterHotKey(_hotkeyWindow.Handle, id, combo.Modifiers, combo.Vk))
            {
                _registeredHotkeys.Add((id, combo.Display));
                Log($"phrase hotkey OK [{phrase.Name}]: {combo.Display}");
            }
            else
            {
                var msg = $"hotkey for '{phrase.Name}' ({combo.Display}) is already in use (Win32 error {Marshal.GetLastWin32Error()})";
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

    // ---------------------------------------------------------------- tray menu

    private void RebuildMenu()
    {
        var menu = new ContextMenuStrip();
        menu.Opening += (_, _) => _menuTarget = GetForegroundWindow();

        menu.Items.Add(new ToolStripMenuItem($"{AppName} - {_config.Phrases.Count} phrases") { Enabled = false });
        menu.Items.Add(new ToolStripSeparator());

        foreach (var phrase in _config.Phrases.Where(p => p is not null).Take(10))
        {
            var label = string.IsNullOrWhiteSpace(phrase.Name) ? "(unnamed)" : phrase.Name;
            if (!string.IsNullOrWhiteSpace(phrase.Hotkey)) label += $"   ({phrase.Hotkey})";
            var captured = phrase;
            menu.Items.Add(new ToolStripMenuItem(label, null, (_, _) => PastePhrase(captured)));
        }

        if (_config.Phrases.Count > 10)
        {
            menu.Items.Add(new ToolStripMenuItem($"\u2026 {_config.Phrases.Count - 10} more in the picker") { Enabled = false });
        }

        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem("Pick phrase\u2026", null, (_, _) => OpenPicker(_menuTarget)));
        menu.Items.Add(new ToolStripMenuItem("Reload phrases", null, (_, _) =>
        {
            try
            {
                LoadConfig();
                RebuildMenu();
                ShowBalloon(AppName, $"Reloaded {_config.Phrases.Count} phrases.", ToolTipIcon.Info);
            }
            catch (Exception ex)
            {
                Log($"reload failed: {ex}");
            }
        }));
        menu.Items.Add(new ToolStripMenuItem("Open config folder", null, (_, _) =>
        {
            try
            {
                Process.Start(new ProcessStartInfo("explorer.exe", $"\"{_dataDir}\"") { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                Log($"open config folder failed: {ex}");
            }
        }));
        menu.Items.Add(new ToolStripMenuItem("Start with Windows", null, (_, _) =>
        {
            _autostartEnabled = !_autostartEnabled;
            WriteAutoStartPreference(_autostartEnabled);
            SyncAutoStart();
            RebuildMenu();
        })
        {
            Checked = _autostartEnabled,
        });
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem("Quit", null, (_, _) => Quit()));

        _tray.ContextMenuStrip = menu;
    }

    private void PastePhrase(Phrase phrase)
    {
        if (string.IsNullOrWhiteSpace(phrase.Text)) return;
        PasteTextIntoForeground(phrase.Text, _menuTarget);
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

    private void Quit()
    {
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

            using var bg = new SolidBrush(Color.FromArgb(52, 73, 94)); // slate blue
            g.FillEllipse(bg, 1, 1, 30, 30);

            using var white = new SolidBrush(Color.White);
            TextRenderer.DrawText(g, "P", new Font("Segoe UI", 19f, FontStyle.Bold, GraphicsUnit.Pixel),
                new Rectangle(0, 3, 32, 26), Color.White,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        }
        return Icon.FromHandle(bmp.GetHicon());
    }
}
