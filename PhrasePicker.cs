using System.Drawing;
using System.Windows.Forms;

namespace PhrasePaste;

/// <summary>
/// Small always-on-top searchable phrase picker. Type to filter, arrows to
/// move, Enter to paste, Esc to close. Opens near the cursor.
/// </summary>
internal sealed class PhrasePicker : Form
{
    private readonly TextBox _search;
    private readonly ListBox _list;
    private readonly List<Phrase> _all;
    private readonly Dictionary<int, Phrase> _indexToPhrase = new();

    public Phrase? Selected { get; private set; }

    public PhrasePicker(List<Phrase> phrases)
    {
        _all = phrases;

        Text = "PhrasePaste";
        FormBorderStyle = FormBorderStyle.FixedToolWindow;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        KeyPreview = true;

        var dark = Color.FromArgb(30, 30, 34);
        var darker = Color.FromArgb(45, 45, 52);
        var light = Color.FromArgb(228, 228, 232);
        var dim = Color.FromArgb(140, 140, 150);

        BackColor = dark;
        ForeColor = light;

        var hint = new Label
        {
            Text = "Type to filter   \u00b7   \u2191\u2193 to move   \u00b7   Enter to paste   \u00b7   Esc to close",
            Dock = DockStyle.Top,
            Height = 22,
            ForeColor = dim,
            BackColor = dark,
            TextAlign = ContentAlignment.MiddleLeft,
            Padding = new Padding(8, 0, 0, 0),
        };

        _search = new TextBox
        {
            Dock = DockStyle.Top,
            BackColor = darker,
            ForeColor = light,
            BorderStyle = BorderStyle.FixedSingle,
            Font = new Font("Segoe UI", 10f),
        };

        _list = new ListBox
        {
            Dock = DockStyle.Fill,
            BackColor = dark,
            ForeColor = light,
            BorderStyle = BorderStyle.None,
            Font = new Font("Segoe UI", 10f),
            IntegralHeight = false,
        };

        Controls.Add(_list);
        Controls.Add(_search);
        Controls.Add(hint);

        _search.TextChanged += (_, _) => ApplyFilter();
        _list.DoubleClick += (_, _) => Choose();
        KeyDown += OnKeyDown;
        Shown += (_, _) =>
        {
            Activate();
            _search.Focus();
        };

        Size = new Size(460, 340);
        PositionNearCursor();
        ApplyFilter();
    }

    public void FocusSearch()
    {
        Activate();
        _search.Focus();
    }

    private void PositionNearCursor()
    {
        var area = Screen.FromPoint(Cursor.Position).WorkingArea;
        var x = Math.Clamp(Cursor.Position.X - Width / 2, area.Left + 8, area.Right - Width - 8);
        var y = Math.Clamp(Cursor.Position.Y + 16, area.Top + 8, area.Bottom - Height - 8);
        Location = new Point(x, y);
    }

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
        }
    }

    private void MoveList(int delta)
    {
        if (_list.Items.Count == 0) return;
        var idx = _list.SelectedIndex < 0
            ? 0
            : Math.Clamp(_list.SelectedIndex + delta, 0, _list.Items.Count - 1);
        _list.SelectedIndex = idx;
    }

    private void ApplyFilter()
    {
        var query = _search.Text.Trim();

        _list.BeginUpdate();
        _list.Items.Clear();
        _indexToPhrase.Clear();

        foreach (var phrase in _all)
        {
            var name = string.IsNullOrWhiteSpace(phrase.Name) ? FallbackName(phrase) : phrase.Name;
            if (query.Length > 0
                && !name.Contains(query, StringComparison.OrdinalIgnoreCase)
                && !phrase.Text.Contains(query, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var label = string.IsNullOrWhiteSpace(phrase.Hotkey) ? name : $"{name}   ({phrase.Hotkey})";
            _indexToPhrase[_list.Items.Add(label)] = phrase;
        }

        _list.EndUpdate();
        _list.SelectedIndex = _list.Items.Count > 0 ? 0 : -1;
    }

    private static string FallbackName(Phrase phrase)
    {
        var line = phrase.Text.Replace("\r", "").Split('\n')
            .FirstOrDefault(l => !string.IsNullOrWhiteSpace(l)) ?? "";
        return line.Length > 30 ? line[..30] + "\u2026" : line;
    }

    private void Choose()
    {
        if (_list.SelectedIndex >= 0 && _indexToPhrase.TryGetValue(_list.SelectedIndex, out var phrase))
        {
            Selected = phrase;
        }
        Close();
    }
}
