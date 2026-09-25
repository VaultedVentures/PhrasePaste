using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace PhrasePaste;

/// <summary>
/// One dark palette for the picker and the editor, so the two windows look
/// like one app. Stock WinForms controls get dark colours plus flat buttons
/// rather than the OS chrome.
/// </summary>
internal static class Theme
{
    public static readonly Color Bg = Color.FromArgb(30, 30, 34);
    public static readonly Color BgAlt = Color.FromArgb(45, 45, 52);
    public static readonly Color BgField = Color.FromArgb(38, 38, 44);
    public static readonly Color Fg = Color.FromArgb(228, 228, 232);
    public static readonly Color Dim = Color.FromArgb(140, 140, 150);
    public static readonly Color Accent = Color.FromArgb(52, 73, 94);
    public static readonly Color Selection = Color.FromArgb(58, 82, 108);
    public static readonly Color Border = Color.FromArgb(66, 66, 76);
    public static readonly Color Warn = Color.FromArgb(230, 168, 96);
    public static readonly Color Good = Color.FromArgb(140, 200, 140);

    public static readonly Font Body = new("Segoe UI", 10f);
    public static readonly Font Small = new("Segoe UI", 8.5f);
    public static readonly Font Label = new("Segoe UI", 9f);

    public static Button FlatButton(string text, int width = 0)
    {
        var button = new Button
        {
            Text = text,
            FlatStyle = FlatStyle.Flat,
            BackColor = BgAlt,
            ForeColor = Fg,
            Font = Body,
            Height = 30,
            UseVisualStyleBackColor = false,
            AutoSize = false,
        };
        button.FlatAppearance.BorderColor = Border;
        button.FlatAppearance.BorderSize = 1;
        button.FlatAppearance.MouseOverBackColor = Selection;

        if (width > 0) button.Width = width;
        else button.AutoSize = true;

        return button;
    }

    public static Label FieldLabel(string text) => new()
    {
        Text = text,
        ForeColor = Dim,
        BackColor = Bg,
        Font = Label,
        AutoSize = true,
    };

    public static TextBox Field(bool multiline = false) => new()
    {
        BackColor = BgField,
        ForeColor = Fg,
        BorderStyle = BorderStyle.FixedSingle,
        Font = Body,
        Multiline = multiline,
        ShortcutsEnabled = true,
    };

    /// <summary>
    /// Drop-down menus (tray + picker) ignore BackColor on their own, they
    /// need a renderer to stop looking like a stock light Windows menu.
    /// </summary>
    public static void MakeDark(ContextMenuStrip menu)
    {
        menu.BackColor = BgAlt;
        menu.ForeColor = Fg;
        menu.Font = Small;
        menu.ShowImageMargin = false;
        menu.Renderer = new ToolStripProfessionalRenderer(new DarkMenuColors());
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    /// <summary>
    /// Dark title bar, so a window is not a light strip sitting on a dark app.
    /// Attribute 20 is the Windows 11 name, 19 the earlier one.
    /// </summary>
    public static void DarkTitleBar(Form form)
    {
        try
        {
            var on = 1;
            if (DwmSetWindowAttribute(form.Handle, 20, ref on, sizeof(int)) != 0)
            {
                DwmSetWindowAttribute(form.Handle, 19, ref on, sizeof(int));
            }
        }
        catch
        {
            // older Windows keeps its default chrome; not worth reporting
        }
    }

    private sealed class DarkMenuColors : ProfessionalColorTable
    {
        public override Color ToolStripDropDownBackground => BgAlt;
        public override Color MenuItemSelected => Selection;
        public override Color MenuItemBorder => Selection;
        public override Color MenuBorder => Border;
        public override Color SeparatorDark => Border;
        public override Color SeparatorLight => Border;
        public override Color ImageMarginGradientBegin => BgAlt;
        public override Color ImageMarginGradientMiddle => BgAlt;
        public override Color ImageMarginGradientEnd => BgAlt;
    }
}
