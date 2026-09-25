using System.Diagnostics;
using System.Drawing;
using System.Windows.Forms;

namespace PhrasePaste;

/// <summary>
/// Text box that records a key combination instead of accepting typed text.
/// Click it and press the keys; Delete/Backspace clears it. Whatever it
/// reports has already round-tripped through <see cref="HotkeyManager"/>'s
/// parser, so a value this control hands back is always storable.
/// </summary>
internal sealed class HotkeyCaptureBox : TextBox
{
    private const int ProbeId = 0x4F00;

    private bool _winDown;

    /// <summary>Raised with a "Ctrl+Alt+1" spec, or "" when the user cleared it.</summary>
    public event Action<string>? ComboCaptured;

    /// <summary>Raised when a key with no name in the config syntax was pressed.</summary>
    public event Action<string>? KeyRejected;

    public HotkeyCaptureBox()
    {
        ReadOnly = true;
        ShortcutsEnabled = false;
        Cursor = Cursors.Hand;
        BackColor = Theme.BgField;
        ForeColor = Theme.Fg;
        BorderStyle = BorderStyle.FixedSingle;
        Font = Theme.Body;
        AutoSize = false;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        // Never let the combination reach the dialog: Ctrl+S must not save,
        // Tab must not leave the box, and the key itself is a candidate.
        e.Handled = true;
        e.SuppressKeyPress = true;

        if (e.KeyCode is Keys.LWin or Keys.RWin)
        {
            _winDown = true;
            return;
        }

        // Wait for a real key; modifiers alone are not a combination.
        if (e.KeyCode is Keys.ControlKey or Keys.ShiftKey or Keys.Menu or Keys.LWin or Keys.RWin) return;

        if (e.KeyCode is Keys.Delete or Keys.Back)
        {
            ComboCaptured?.Invoke("");
            return;
        }

        var mods = ModifierKeys;
        var formatted = HotkeyManager.Format(
            e.KeyCode,
            mods.HasFlag(Keys.Control),
            mods.HasFlag(Keys.Alt),
            mods.HasFlag(Keys.Shift),
            _winDown);

        if (formatted is null)
        {
            KeyRejected?.Invoke(e.KeyCode.ToString());
            return;
        }

        if (!HotkeyManager.TryParse(formatted, out _, out _))
        {
            KeyRejected?.Invoke(e.KeyCode.ToString());
            return;
        }

        ComboCaptured?.Invoke(formatted);
    }

    protected override void OnKeyUp(KeyEventArgs e)
    {
        if (e.KeyCode is Keys.LWin or Keys.RWin) _winDown = false;
        base.OnKeyUp(e);
    }

    /// <summary>Clear the combination and report the change.</summary>
    public void ClearCombo()
    {
        Text = "";
        ComboCaptured?.Invoke("");
    }

    /// <summary>The probe handle/id this box uses when asking Windows if a combo is free.</summary>
    public static int ProbeHotkeyId => ProbeId;
}

/// <summary>
/// The phrase manager. Left: the phrase list. Right: everything about the
/// selected phrase, plus the two window hotkeys. Hotkeys are applied the
/// moment you save, so there is no "reload" step and no YAML to hand-write.
/// </summary>
internal sealed class PhraseEditor : Form
{
    private readonly PhraseConfig _config;
    private readonly string _configPath;
    private readonly Func<PhraseConfig, string?> _save;
    private readonly List<Phrase> _view = new();

    /// <summary>
    /// Combinations this app already has registered. The running app owns its
    /// own hotkeys, so probing for one of those always fails and would make
    /// every working phrase look like a conflict.
    /// </summary>
    private readonly IReadOnlyCollection<HotkeyCombo> _liveCombos;

    private readonly ListBox _list;
    private readonly TextBox _name;
    private readonly TextBox _text;
    private readonly HotkeyCaptureBox _hotkey;
    private readonly HotkeyCaptureBox _pickerHotkey;
    private readonly HotkeyCaptureBox _captureHotkey;
    private readonly Label _hotkeyHint;
    private readonly Label _pickerHint;
    private readonly Label _captureHint;
    private readonly Label _status;

    private Phrase? _current;
    private bool _loading;
    private bool _dirty;

    /// <summary>True once the user saved at least once; tells the caller to adopt the config.</summary>
    public bool Saved { get; private set; }

    /// <summary>The edited config. Only meaningful when <see cref="Saved"/> is true.</summary>
    public PhraseConfig Config => _config;

    public PhraseEditor(
        PhraseConfig working,
        Phrase? select,
        string configPath,
        Func<PhraseConfig, string?> save,
        IReadOnlyCollection<HotkeyCombo> liveCombos)
    {
        _config = working;
        _configPath = configPath;
        _save = save;
        _liveCombos = liveCombos;

        Text = "PhrasePaste - phrases";
        BackColor = Theme.Bg;
        ForeColor = Theme.Fg;
        Font = Theme.Body;
        ClientSize = new Size(940, 640);
        MinimumSize = new Size(860, 580);
        StartPosition = FormStartPosition.CenterScreen;
        KeyPreview = true;

        // ---- centre: the fields
        var fields = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            BackColor = Theme.Bg,
            ColumnCount = 1,
            Padding = new Padding(16, 12, 16, 4),
        };
        fields.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));

        _hotkeyHint = MakeHint();
        _pickerHint = MakeHint();
        _captureHint = MakeHint();

        _name = Theme.Field();
        _name.Dock = DockStyle.Fill;
        _name.TextChanged += OnFieldChanged;

        _hotkey = NewCaptureBox(out var hotkeyRow);
        _hotkey.ComboCaptured += combo => OnHotkeyCaptured(combo, _hotkey, HotkeySlot.Phrase);
        _hotkey.KeyRejected += key => ShowHint(_hotkeyHint, $"\"{key}\" has no name in the hotkey syntax.", Theme.Warn);

        _pickerHotkey = NewCaptureBox(out var pickerRow);
        _pickerHotkey.ComboCaptured += combo => OnHotkeyCaptured(combo, _pickerHotkey, HotkeySlot.Picker);
        _pickerHotkey.KeyRejected += key => ShowHint(_pickerHint, $"\"{key}\" has no name in the hotkey syntax.", Theme.Warn);

        _captureHotkey = NewCaptureBox(out var captureRow);
        _captureHotkey.ComboCaptured += combo => OnHotkeyCaptured(combo, _captureHotkey, HotkeySlot.Capture);
        _captureHotkey.KeyRejected += key => ShowHint(_captureHint, $"\"{key}\" has no name in the hotkey syntax.", Theme.Warn);

        _text = Theme.Field(multiline: true);
        _text.Dock = DockStyle.Fill;
        _text.ScrollBars = ScrollBars.Vertical;
        _text.AcceptsReturn = true;
        _text.AcceptsTab = true;
        _text.TextChanged += OnFieldChanged;

        AddRow(fields, Theme.FieldLabel("Name"), SizeType.AutoSize, 0);
        AddRow(fields, _name, SizeType.Absolute, 30);
        AddRow(fields, Theme.FieldLabel("Hotkey"), SizeType.AutoSize, 0);
        AddRow(fields, hotkeyRow, SizeType.Absolute, 32);
        AddRow(fields, _hotkeyHint, SizeType.Absolute, 20);
        AddRow(fields, Theme.FieldLabel("Text"), SizeType.AutoSize, 0);
        AddRow(fields, _text, SizeType.Percent, 100);
        AddRow(fields, Theme.FieldLabel("Picker hotkey (opens this list from anywhere)"), SizeType.AutoSize, 0);
        AddRow(fields, pickerRow, SizeType.Absolute, 32);
        AddRow(fields, _pickerHint, SizeType.Absolute, 20);
        AddRow(fields, Theme.FieldLabel("Capture-selection hotkey (turns the selected text into a phrase)"), SizeType.AutoSize, 0);
        AddRow(fields, captureRow, SizeType.Absolute, 32);
        AddRow(fields, _captureHint, SizeType.Absolute, 20);

        // ---- left: the list
        var left = new Panel { Dock = DockStyle.Left, Width = 350, BackColor = Theme.Bg, Padding = new Padding(12, 12, 6, 12) };

        _list = new ListBox
        {
            Dock = DockStyle.Fill,
            BackColor = Theme.BgField,
            ForeColor = Theme.Fg,
            BorderStyle = BorderStyle.FixedSingle,
            Font = Theme.Body,
            IntegralHeight = false,
            DrawMode = DrawMode.OwnerDrawFixed,
            ItemHeight = 24,
        };
        _list.DrawItem += DrawListItem;
        _list.SelectedIndexChanged += OnListSelectionChanged;

        var listTop = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 38, BackColor = Theme.Bg, Padding = new Padding(0, 0, 0, 4) };
        listTop.Controls.Add(Button("New", () => AddPhrase(new Phrase { Name = "New phrase" }, focusName: true)));
        listTop.Controls.Add(Button("Duplicate", () =>
        {
            if (_current is null) return;
            var copy = _current.Clone();
            copy.Name = _current.DisplayName() + " (copy)";
            AddPhrase(copy, focusName: true);
        }));
        listTop.Controls.Add(Button("Delete", DeleteCurrent));

        var listBottom = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 38, BackColor = Theme.Bg, Padding = new Padding(0, 4, 0, 0) };
        listBottom.Controls.Add(Button("Move up", () => MoveCurrent(-1)));
        listBottom.Controls.Add(Button("Move down", () => MoveCurrent(1)));

        left.Controls.Add(_list);
        left.Controls.Add(listTop);
        left.Controls.Add(listBottom);

        // ---- top: what this window is
        var top = new Panel { Dock = DockStyle.Top, Height = 52, BackColor = Theme.Bg, Padding = new Padding(16, 14, 16, 0) };
        top.Controls.Add(new Label
        {
            Text = "Phrases",
            Font = new Font("Segoe UI", 13f),
            ForeColor = Theme.Fg,
            AutoSize = true,
            Location = new Point(14, 12),
        });
        top.Controls.Add(new Label
        {
            Text = "Hotkeys apply as soon as you save. Nothing else to reload.",
            Font = Theme.Small,
            ForeColor = Theme.Dim,
            AutoSize = true,
            Location = new Point(102, 20),
        });

        // ---- bottom: actions
        var bottom = new Panel { Dock = DockStyle.Bottom, Height = 58, BackColor = Theme.Bg, Padding = new Padding(16, 10, 16, 12) };

        var actions = new FlowLayoutPanel { Dock = DockStyle.Left, AutoSize = true, BackColor = Theme.Bg };
        actions.Controls.Add(Button("Save", SaveNow, primary: true));
        actions.Controls.Add(Button("Close", Close));
        actions.Controls.Add(Button("Edit raw file", OpenRawFile));

        _status = new Label
        {
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleRight,
            ForeColor = Theme.Dim,
            Font = Theme.Small,
            Padding = new Padding(0, 0, 4, 0),
        };

        bottom.Controls.Add(_status);
        bottom.Controls.Add(actions);

        // Docking resolves from the highest index first, so Fill goes in first.
        Controls.Add(fields);
        Controls.Add(left);
        Controls.Add(bottom);
        Controls.Add(top);

        RefreshList(select: select ?? _config.Phrases.FirstOrDefault());
        UpdateWindowTitle();
        LoadGlobals();
    }

    // ------------------------------------------------------------ construction

    private enum HotkeySlot { Phrase, Picker, Capture }

    private static void AddRow(TableLayoutPanel table, Control control, SizeType size, int pixels)
    {
        control.Margin = new Padding(0, 2, 0, 2);
        table.RowStyles.Add(new RowStyle(size, pixels));
        table.Controls.Add(control);
    }

    private static Label MakeHint() => new()
    {
        Dock = DockStyle.Fill,
        ForeColor = Theme.Dim,
        Font = Theme.Small,
        Text = "",
        TextAlign = ContentAlignment.MiddleLeft,
    };

    private static Button Button(string text, Action onClick, bool primary = false)
    {
        var button = Theme.FlatButton(text);
        if (primary)
        {
            button.BackColor = Theme.Accent;
            button.Font = new Font("Segoe UI", 10f, FontStyle.Bold);
        }
        button.Margin = new Padding(0, 0, 8, 0);
        button.Click += (_, _) => onClick();
        return button;
    }

    private static HotkeyCaptureBox NewCaptureBox(out Panel row)
    {
        var box = new HotkeyCaptureBox { Dock = DockStyle.Fill };
        var clear = Theme.FlatButton("Clear", 70);
        clear.Dock = DockStyle.Right;
        clear.Margin = new Padding(0);

        row = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Bg };
        row.Controls.Add(box);
        row.Controls.Add(clear);
        clear.Click += (_, _) => box.ClearCombo();
        return box;
    }

    // ------------------------------------------------------------ list

    private void RefreshList(Phrase? select)
    {
        _loading = true;
        try
        {
            _list.Items.Clear();
            _view.Clear();
            foreach (var phrase in _config.Phrases)
            {
                _view.Add(phrase);
                _list.Items.Add(ListLabel(phrase));
            }

            var index = select is null ? -1 : _config.Phrases.IndexOf(select);
            _list.SelectedIndex = index >= 0 && index < _list.Items.Count ? index : (_list.Items.Count > 0 ? 0 : -1);
        }
        finally
        {
            _loading = false;
        }

        ShowCurrent();
    }

    private static string ListLabel(Phrase phrase)
    {
        var label = phrase.DisplayName();
        return string.IsNullOrWhiteSpace(phrase.Hotkey) ? label : $"{label}   ({phrase.Hotkey})";
    }

    /// <summary>Draws the list itself, so the selection is the app's slate rather than stock Windows blue.</summary>
    private void DrawListItem(object? sender, DrawItemEventArgs e)
    {
        if (e.Index < 0 || e.Index >= _list.Items.Count) return;

        var selected = (e.State & DrawItemState.Selected) != 0;
        using (var back = new SolidBrush(selected ? Theme.Selection : Theme.BgField))
        {
            e.Graphics.FillRectangle(back, e.Bounds);
        }

        var text = _list.Items[e.Index]?.ToString() ?? "";
        TextRenderer.DrawText(e.Graphics, text, Theme.Body,
            new Rectangle(e.Bounds.X + 7, e.Bounds.Y, Math.Max(20, e.Bounds.Width - 11), e.Bounds.Height),
            selected ? Color.White : Theme.Fg,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
    }

    private void RefreshCurrentRow()
    {
        if (_current is null) return;
        var index = _view.IndexOf(_current);
        if (index < 0) return;

        _loading = true;
        try
        {
            _list.Items[index] = ListLabel(_current);
            _list.SelectedIndex = index;
        }
        finally
        {
            _loading = false;
        }
    }

    private void OnListSelectionChanged(object? sender, EventArgs e)
    {
        if (_loading) return;
        ShowCurrent();
    }

    private void ShowCurrent()
    {
        var index = _list.SelectedIndex;
        _current = index >= 0 && index < _view.Count ? _view[index] : null;

        _loading = true;
        try
        {
            _name.Text = _current?.Name ?? "";
            _text.Text = _current?.Text ?? "";
            _hotkey.Text = _current?.Hotkey ?? "";
            _name.Enabled = _text.Enabled = _hotkey.Enabled = _current is not null;
        }
        finally
        {
            _loading = false;
        }

        if (_current is null)
        {
            ShowHint(_hotkeyHint, "", Theme.Dim);
            return;
        }

        var hotkeyCheck = CheckCombo(_current.Hotkey, HotkeySlot.Phrase, _current);
        ShowHint(_hotkeyHint,
            string.IsNullOrWhiteSpace(_current.Hotkey)
                ? "No hotkey: picker and tray menu only."
                : hotkeyCheck.Problem ?? "Ready to use.",
            hotkeyCheck.Problem is null ? Theme.Dim : Theme.Warn);
    }

    // ------------------------------------------------------------ editing

    private void OnFieldChanged(object? sender, EventArgs e)
    {
        if (_loading || _current is null) return;

        _current.Name = _name.Text;
        _current.Text = _text.Text;
        RefreshCurrentRow();
        MarkDirty();
    }

    private void MarkDirty()
    {
        _dirty = true;
        UpdateWindowTitle();
    }

    private void UpdateWindowTitle() => Text = _dirty ? "PhrasePaste - phrases *" : "PhrasePaste - phrases";

    private void AddPhrase(Phrase phrase, bool focusName)
    {
        _config.Phrases.Add(phrase);
        RefreshList(select: phrase);
        MarkDirty();

        if (focusName)
        {
            _name.Focus();
            _name.SelectAll();
        }
    }

    private void DeleteCurrent()
    {
        if (_current is null) return;

        var answer = MessageBox.Show(
            this,
            $"Delete \"{_current.DisplayName()}\"?",
            "PhrasePaste",
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Warning,
            MessageBoxDefaultButton.Button2);

        if (answer != DialogResult.Yes) return;

        var index = _config.Phrases.IndexOf(_current);
        if (index < 0) return;

        _config.Phrases.RemoveAt(index);
        var next = _config.Phrases.Count == 0
            ? null
            : _config.Phrases[Math.Min(index, _config.Phrases.Count - 1)];

        RefreshList(select: next);
        MarkDirty();
    }

    private void MoveCurrent(int delta)
    {
        if (_current is null) return;

        var from = _config.Phrases.IndexOf(_current);
        var to = from + delta;
        if (from < 0 || to < 0 || to >= _config.Phrases.Count) return;

        (_config.Phrases[from], _config.Phrases[to]) = (_config.Phrases[to], _config.Phrases[from]);
        RefreshList(select: _current);
        MarkDirty();
    }

    // ------------------------------------------------------------ hotkeys

    private void LoadGlobals()
    {
        _loading = true;
        try
        {
            _pickerHotkey.Text = _config.PopupHotkey;
            _captureHotkey.Text = _config.CaptureHotkey;
        }
        finally
        {
            _loading = false;
        }

        var pickerCheck = CheckCombo(_config.PopupHotkey, HotkeySlot.Picker, null);
        ShowHint(_pickerHint,
            pickerCheck.Problem ?? (string.IsNullOrWhiteSpace(_config.PopupHotkey) ? "No picker hotkey: tray icon only." : "Opens the phrase list from anywhere."),
            pickerCheck.Problem is null ? Theme.Dim : Theme.Warn);

        var captureCheck = CheckCombo(_config.CaptureHotkey, HotkeySlot.Capture, null);
        ShowHint(_captureHint,
            string.IsNullOrWhiteSpace(_config.CaptureHotkey)
                ? "Not set: use the tray menu instead."
                : captureCheck.Problem ?? "Grabs the selected text from any app.",
            captureCheck.Problem is null ? Theme.Dim : Theme.Warn);
    }

    private void OnHotkeyCaptured(string combo, HotkeyCaptureBox box, HotkeySlot slot)
    {
        if (_loading) return;
        if (slot == HotkeySlot.Phrase && _current is null) return;

        var check = CheckCombo(combo, slot, _current);

        if (check.Problem is not null)
        {
            // Rejected combinations never reach the config, so the box snaps
            // back to what is actually stored and the hint says why.
            RevertBox(box, slot);
            var hint = slot switch
            {
                HotkeySlot.Picker => _pickerHint,
                HotkeySlot.Capture => _captureHint,
                _ => _hotkeyHint,
            };
            ShowHint(hint, check.Problem, Theme.Warn);
            SystemSoundsBeep();
            return;
        }

        _loading = true;
        try
        {
            box.Text = combo;
            switch (slot)
            {
                case HotkeySlot.Phrase when _current is not null:
                    _current.Hotkey = combo;
                    RefreshCurrentRow();
                    ShowHint(_hotkeyHint, combo.Length == 0 ? "No hotkey: picker and tray menu only." : "Ready to use.", Theme.Dim);
                    break;
                case HotkeySlot.Picker:
                    _config.PopupHotkey = combo;
                    ShowHint(_pickerHint, combo.Length == 0 ? "No picker hotkey: tray icon only." : "Opens the phrase list from anywhere.", Theme.Dim);
                    break;
                case HotkeySlot.Capture:
                    _config.CaptureHotkey = combo;
                    ShowHint(_captureHint, combo.Length == 0 ? "Not set: use the tray menu instead." : "Grabs the selected text from any app.", Theme.Dim);
                    break;
            }
        }
        finally
        {
            _loading = false;
        }

        MarkDirty();
    }

    private void RevertBox(HotkeyCaptureBox box, HotkeySlot slot)
    {
        _loading = true;
        try
        {
            box.Text = slot switch
            {
                HotkeySlot.Picker => _config.PopupHotkey,
                HotkeySlot.Capture => _config.CaptureHotkey,
                _ => _current?.Hotkey ?? "",
            };
        }
        finally
        {
            _loading = false;
        }
    }

    /// <summary>
    /// <see cref="Problem"/> is null when the combination is usable.
    /// <see cref="Blocks"/> says whether it must stop a save: a clash inside
    /// this file would leave the config broken, while a combination another app
    /// owns is worth saying out loud but not worth refusing to write over.
    /// </summary>
    private readonly record struct ComboCheck(string? Problem, bool Blocks)
    {
        public static readonly ComboCheck Ok = new(null, false);

        public static ComboCheck Broken(string problem) => new(problem, true);

        public static ComboCheck Taken(string problem) => new(problem, false);
    }

    /// <summary>
    /// Rejects unnamed keys, modifier-free combinations (a bare "A" would
    /// hijack typing system-wide), combinations already spoken for inside the
    /// config, and combinations another app owns.
    /// </summary>
    private ComboCheck CheckCombo(string? combo, HotkeySlot slot, Phrase? ownerPhrase)
    {
        if (string.IsNullOrWhiteSpace(combo)) return ComboCheck.Ok;

        if (!HotkeyManager.TryParse(combo, out var parsed, out var error))
        {
            return ComboCheck.Broken($"\"{combo}\" can't be used: {error}.");
        }

        if (parsed.Modifiers == 0)
        {
            return ComboCheck.Broken("Needs at least one of Ctrl, Alt, Shift or Win.");
        }

        foreach (var phrase in _config.Phrases)
        {
            if (ReferenceEquals(phrase, ownerPhrase)) continue;
            if (SameCombo(phrase.Hotkey, parsed)) return ComboCheck.Broken($"Already used by \"{phrase.DisplayName()}\".");
        }

        if (slot != HotkeySlot.Picker && SameCombo(_config.PopupHotkey, parsed))
        {
            return ComboCheck.Broken("Already used by the picker hotkey.");
        }
        if (slot != HotkeySlot.Capture && SameCombo(_config.CaptureHotkey, parsed))
        {
            return ComboCheck.Broken("Already used by the capture-selection hotkey.");
        }

        // Our own registrations hold their combinations, so probe only for
        // combinations this app is not already sitting on.
        if (IsHeldByApp(parsed)) return ComboCheck.Ok;

        return HotkeyManager.IsComboFree(Handle, HotkeyCaptureBox.ProbeHotkeyId, parsed.Modifiers, parsed.Vk)
            ? ComboCheck.Ok
            : ComboCheck.Taken("Another app is already using that combination.");
    }

    private bool IsHeldByApp(HotkeyCombo combo) =>
        _liveCombos.Any(held => held.Modifiers == combo.Modifiers && held.Vk == combo.Vk);

    private static bool SameCombo(string? spec, HotkeyCombo other) =>
        HotkeyManager.TryParse(spec, out var parsed, out _)
        && parsed.Modifiers == other.Modifiers
        && parsed.Vk == other.Vk;

    private static void ShowHint(Label label, string text, Color colour)
    {
        label.Text = text;
        label.ForeColor = colour;
    }

    private static void SystemSoundsBeep()
    {
        try
        {
            System.Media.SystemSounds.Beep.Play();
        }
        catch
        {
            // no sound card / no message pump: not worth reporting
        }
    }

    // ------------------------------------------------------------ saving

    private void SaveNow()
    {
        if (_current is not null)
        {
            _current.Name = _name.Text;
            _current.Text = _text.Text;
        }

        var blocking = new List<(string Message, Phrase? Phrase)>();
        var taken = new List<string>();

        void Examine(string label, string? combo, HotkeySlot slot, Phrase? owner)
        {
            var check = CheckCombo(combo, slot, owner);
            if (check.Problem is null) return;

            var message = $"{label}: {check.Problem}";
            if (check.Blocks) blocking.Add((message, owner));
            else taken.Add(message);
        }

        Examine("Picker hotkey", _config.PopupHotkey, HotkeySlot.Picker, null);
        Examine("Capture-selection hotkey", _config.CaptureHotkey, HotkeySlot.Capture, null);
        foreach (var phrase in _config.Phrases)
        {
            Examine($"\"{phrase.DisplayName()}\"", phrase.Hotkey, HotkeySlot.Phrase, phrase);
        }

        // A clash inside the file would be written and then silently dropped at
        // registration, so that stops the save.
        if (blocking.Count > 0)
        {
            var (message, phrase) = blocking[0];
            ShowHint(_hotkeyHint, message, Theme.Warn);
            var extra = blocking.Count > 1 ? $"\n\n(and {blocking.Count - 1} more)" : "";
            MessageBox.Show(this,
                message + extra,
                "PhrasePaste",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);

            var index = phrase is null ? -1 : _config.Phrases.IndexOf(phrase);
            if (index >= 0) _list.SelectedIndex = index;
            return;
        }

        var error = _save(_config);
        if (error is not null)
        {
            _status.ForeColor = Theme.Warn;
            _status.Text = "Not saved";
            MessageBox.Show(this, error, "PhrasePaste", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }

        Saved = true;
        _dirty = false;
        UpdateWindowTitle();
        _status.ForeColor = taken.Count == 0 ? Theme.Good : Theme.Warn;
        _status.Text = taken.Count == 0
            ? $"Saved to {Path.GetFileName(_configPath)}"
            : $"Saved, with {taken.Count} hotkey(s) another app owns";

        // Someone else's hotkey is worth reporting but is not a reason to
        // withhold the file: the app just skips registering that one.
        if (taken.Count > 0)
        {
            MessageBox.Show(this,
                "Saved, but these combinations are already owned by another app, so they will not fire:\n\n"
                + string.Join("\n", taken)
                + "\n\nPick different ones if you want them to work.",
                "PhrasePaste",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
        }
    }

    private void OpenRawFile()
    {
        try
        {
            Process.Start(new ProcessStartInfo(_configPath) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            MessageBox.Show(this,
                $"Couldn't open {_configPath}\n\n{ex.Message}",
                "PhrasePaste",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        }
    }

    // ------------------------------------------------------------ window

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        Theme.DarkTitleBar(this);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        // The capture box consumes its own keys; anything typed there is a
        // hotkey, not a shortcut.
        if (ActiveControl is HotkeyCaptureBox) return;

        if (e.Control && e.KeyCode == Keys.S)
        {
            SaveNow();
            e.Handled = true;
            return;
        }

        if (e.KeyCode == Keys.Escape)
        {
            Close();
            e.Handled = true;
            return;
        }

        base.OnKeyDown(e);
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (_dirty && e.CloseReason == CloseReason.UserClosing)
        {
            var answer = MessageBox.Show(
                this,
                "You changed phrases but haven't saved.",
                "PhrasePaste",
                MessageBoxButtons.YesNoCancel,
                MessageBoxIcon.Question);

            switch (answer)
            {
                case DialogResult.Yes:
                    SaveNow();
                    if (_dirty) e.Cancel = true;
                    break;
                case DialogResult.No:
                    break;
                default:
                    e.Cancel = true;
                    break;
            }
        }

        base.OnFormClosing(e);
    }
}
