using System.Drawing;
using System.Windows.Forms;

namespace PhrasePaste;

/// <summary>What the user asked the picker to do.</summary>
internal enum PickerAction
{
    None,
    Paste,
    Copy,
    New,
    Edit,
    Delete,
}

/// <summary>
/// The phrase list, floating next to the cursor. Each row shows the name, a
/// one-line preview of what will actually be pasted, and the hotkey if it has
/// one, which is what makes two similar phrases distinguishable before you
/// commit to one. Type to filter, Enter pastes, right-click for the rest.
/// </summary>
internal sealed class PhrasePicker : Form
{
    private const int RowHeight = 46;

    private readonly List<Phrase> _all;
    private readonly List<Phrase?> _view = new();
    private readonly TextBox _search;
    private readonly ListBox _list;
    private readonly Label _count;
    private readonly ContextMenuStrip _menu;
    private string _emptyMessage = "No matches";

    public PickerAction Action { get; private set; } = PickerAction.None;
    public Phrase? Target { get; private set; }

    public PhrasePicker(List<Phrase> phrases)
    {
        _all = phrases;

        Text = "PhrasePaste";
        FormBorderStyle = FormBorderStyle.FixedToolWindow;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        KeyPreview = true;
        BackColor = Theme.Bg;
        ForeColor = Theme.Fg;
        ClientSize = new Size(520, 400);

        var header = new Panel { Dock = DockStyle.Top, Height = 24, BackColor = Theme.Bg };
        var hint = new Label
        {
            Text = "Type to filter   \u00b7   \u2191\u2193   \u00b7   Enter pastes   \u00b7   right-click for more",
            Dock = DockStyle.Fill,
            ForeColor = Theme.Dim,
            BackColor = Theme.Bg,
            Font = Theme.Small,
            TextAlign = ContentAlignment.MiddleLeft,
            Padding = new Padding(10, 0, 0, 0),
        };
        _count = new Label
        {
            Dock = DockStyle.Right,
            Width = 90,
            ForeColor = Theme.Dim,
            BackColor = Theme.Bg,
            Font = Theme.Small,
            TextAlign = ContentAlignment.MiddleRight,
            Padding = new Padding(0, 0, 10, 0),
        };
        header.Controls.Add(hint);
        header.Controls.Add(_count);

        _search = new TextBox
        {
            Dock = DockStyle.Top,
            BackColor = Theme.BgAlt,
            ForeColor = Theme.Fg,
            BorderStyle = BorderStyle.FixedSingle,
            Font = Theme.Body,
        };

        _list = new ListBox
        {
            Dock = DockStyle.Fill,
            BackColor = Theme.Bg,
            ForeColor = Theme.Fg,
            BorderStyle = BorderStyle.None,
            Font = Theme.Body,
            IntegralHeight = false,
            DrawMode = DrawMode.OwnerDrawFixed,
            ItemHeight = RowHeight,
        };
        _list.DrawItem += DrawRow;
        _list.DoubleClick += (_, _) => Choose();
        _list.MouseDown += OnListMouseDown;
        _list.MouseUp += OnListMouseUp;

        _menu = new ContextMenuStrip();
        Theme.MakeDark(_menu);
        _menu.Items.Add(MenuItem("Paste now", PickerAction.Paste));
        _menu.Items.Add(MenuItem("Copy to clipboard", PickerAction.Copy));
        _menu.Items.Add(MenuItem("Edit phrase\u2026", PickerAction.Edit));
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add(MenuItem("New phrase\u2026", PickerAction.New));
        _menu.Items.Add(MenuItem("Delete phrase", PickerAction.Delete));

        Controls.Add(_list);
        Controls.Add(_search);
        Controls.Add(header);

        _search.TextChanged += (_, _) => ApplyFilter();
        KeyDown += OnKeyDown;
        Shown += (_, _) =>
        {
            Activate();
            _search.Focus();
        };

        PositionNearCursor();
        ApplyFilter();
    }

    public void FocusSearch()
    {
        Activate();
        _search.Focus();
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        Theme.DarkTitleBar(this);
    }

    private ToolStripMenuItem MenuItem(string text, PickerAction action) => new(text, null, (_, _) => Request(action));

    private void PositionNearCursor()
    {
        var area = Screen.FromPoint(Cursor.Position).WorkingArea;
        var x = Math.Clamp(Cursor.Position.X - Width / 2, area.Left + 8, area.Right - Width - 8);
        var y = Math.Clamp(Cursor.Position.Y + 16, area.Top + 8, area.Bottom - Height - 8);
        Location = new Point(x, y);
    }

    // ------------------------------------------------------------ keyboard

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        switch (e.KeyCode)
        {
            case Keys.Enter:
                Choose();
                e.Handled = true;
                break;

            case Keys.Escape:
                Close();
                e.Handled = true;
                break;

            case Keys.Down:
                MoveList(1);
                e.Handled = true;
                break;

            case Keys.Up:
                MoveList(-1);
                e.Handled = true;
                break;

            case Keys.N when e.Control:
                Request(PickerAction.New);
                e.Handled = true;
                break;

            case Keys.E when e.Control:
            case Keys.F2:
                Request(PickerAction.Edit);
                e.Handled = true;
                break;
        }
    }

    private void MoveList(int delta)
    {
        if (_list.Items.Count == 0) return;
        var index = _list.SelectedIndex < 0
            ? 0
            : Math.Clamp(_list.SelectedIndex + delta, 0, _list.Items.Count - 1);
        _list.SelectedIndex = index;
    }

    // ------------------------------------------------------------ mouse

    private void OnListMouseDown(object? sender, MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Right) return;

        var index = _list.IndexFromPoint(e.Location);
        if (index < 0 || index >= _view.Count || _view[index] is null) return;

        _list.SelectedIndex = index;
        _menu.Show(_list, e.Location);
    }

    private void OnListMouseUp(object? sender, MouseEventArgs e)
    {
        // Left-clicking a row that is already selected should paste, the way
        // a plain list of items behaves.
        if (e.Button != MouseButtons.Left) return;

        var index = _list.IndexFromPoint(e.Location);
        if (index < 0 || index != _list.SelectedIndex) return;
        Choose();
    }

    // ------------------------------------------------------------ filtering

    private void ApplyFilter()
    {
        var query = _search.Text.Trim();

        var scored = new List<(Phrase Phrase, int Score)>();
        foreach (var phrase in _all)
        {
            var score = Score(phrase, query);
            if (score != int.MinValue) scored.Add((phrase, score));
        }

        _view.Clear();
        foreach (var (phrase, _) in query.Length == 0
                     ? scored.Select(s => (s.Phrase, 0))
                     : scored.OrderByDescending(s => s.Score))
        {
            _view.Add(phrase);
        }

        if (_view.Count == 0)
        {
            _emptyMessage = _all.Count == 0
                ? "No phrases yet \u2014 Ctrl+N adds one"
                : "No matches \u2014 Ctrl+N adds a new phrase";
            _view.Add(null);
        }

        _list.BeginUpdate();
        _list.Items.Clear();
        for (var i = 0; i < _view.Count; i++) _list.Items.Add(i);
        _list.EndUpdate();

        _list.SelectedIndex = _view[0] is null ? -1 : 0;

        _count.Text = query.Length == 0
            ? $"{_all.Count} phrase{(_all.Count == 1 ? "" : "s")}"
            : $"{_view.Count(p => p is not null)} of {_all.Count}";
    }

    /// <summary>
    /// Substring beats fuzzy, name beats body: typing "sig" should put
    /// "Sign-off" above a phrase that merely contains "sig" in its text, and
    /// typing "sgn" should still find "Sign-off".
    /// </summary>
    private static int Score(Phrase phrase, string query)
    {
        if (query.Length == 0) return 0;

        var name = phrase.DisplayName();
        var score = Math.Max(
            SubstringScore(name, query, 3000),
            SubstringScore(phrase.Text, query, 1000));

        return Math.Max(score, FuzzyScore(name, query, 500));
    }

    private static int SubstringScore(string haystack, string needle, int weight)
    {
        if (string.IsNullOrEmpty(haystack)) return int.MinValue;
        var index = haystack.IndexOf(needle, StringComparison.OrdinalIgnoreCase);
        return index < 0 ? int.MinValue : weight - index;
    }

    private static int FuzzyScore(string haystack, string needle, int weight)
    {
        if (string.IsNullOrEmpty(haystack)) return int.MinValue;

        var cursor = 0;
        var gaps = 0;
        foreach (var ch in needle)
        {
            var found = -1;
            for (var i = cursor; i < haystack.Length; i++)
            {
                if (char.ToLowerInvariant(haystack[i]) == char.ToLowerInvariant(ch))
                {
                    found = i;
                    break;
                }
            }

            if (found < 0) return int.MinValue;
            gaps += found - cursor;
            cursor = found + 1;
        }

        return weight - gaps;
    }

    // ------------------------------------------------------------ drawing

    private void DrawRow(object? sender, DrawItemEventArgs e)
    {
        if (e.Index < 0 || e.Index >= _view.Count) return;

        var phrase = _view[e.Index];
        var selected = (e.State & DrawItemState.Selected) != 0;
        var bounds = e.Bounds;

        using (var back = new SolidBrush(selected ? Theme.Selection : Theme.Bg))
        {
            e.Graphics.FillRectangle(back, bounds);
        }

        using (var line = new Pen(Theme.Border))
        {
            e.Graphics.DrawLine(line, bounds.Left + 10, bounds.Bottom - 1, bounds.Right - 10, bounds.Bottom - 1);
        }

        if (phrase is null)
        {
            TextRenderer.DrawText(e.Graphics, _emptyMessage, Theme.Small,
                new Rectangle(bounds.X + 14, bounds.Y, bounds.Width - 28, bounds.Height),
                Theme.Dim, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
            return;
        }

        var hotkeyWidth = 0;
        if (!string.IsNullOrWhiteSpace(phrase.Hotkey))
        {
            hotkeyWidth = TextRenderer.MeasureText(phrase.Hotkey, Theme.Small).Width + 16;
            TextRenderer.DrawText(e.Graphics, phrase.Hotkey, Theme.Small,
                new Rectangle(bounds.Right - hotkeyWidth, bounds.Y, hotkeyWidth - 12, bounds.Height),
                selected ? Theme.Fg : Theme.Dim,
                TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
        }

        var textWidth = Math.Max(60, bounds.Width - 24 - hotkeyWidth);
        TextRenderer.DrawText(e.Graphics, phrase.DisplayName(), Theme.Body,
            new Rectangle(bounds.X + 14, bounds.Y + 6, textWidth, 18), Theme.Fg,
            TextFormatFlags.Left | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);

        TextRenderer.DrawText(e.Graphics, phrase.Preview(), Theme.Small,
            new Rectangle(bounds.X + 14, bounds.Y + 24, textWidth, 16), Theme.Dim,
            TextFormatFlags.Left | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
    }

    // ------------------------------------------------------------ result

    private Phrase? CurrentPhrase()
    {
        var index = _list.SelectedIndex;
        return index >= 0 && index < _view.Count ? _view[index] : null;
    }

    private void Choose()
    {
        var phrase = CurrentPhrase();
        if (phrase is null) return;
        Finish(PickerAction.Paste, phrase);
    }

    private void Request(PickerAction action)
    {
        if (action == PickerAction.New)
        {
            Finish(PickerAction.New, null);
            return;
        }

        var phrase = CurrentPhrase();
        if (phrase is null) return;

        Finish(action, phrase);
    }

    private void Finish(PickerAction action, Phrase? phrase)
    {
        Action = action;
        Target = phrase;
        Close();
    }
}
