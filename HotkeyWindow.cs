using System.Windows.Forms;

namespace PhrasePaste;

/// <summary>
/// Hidden window that receives WM_HOTKEY messages from RegisterHotKey.
/// Same pattern as Clippa's hidden message window.
/// </summary>
internal sealed class HotkeyWindow : NativeWindow
{
    private const int WmHotkey = 0x0312;

    public event Action<int>? HotKeyPressed;

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WmHotkey)
        {
            HotKeyPressed?.Invoke(m.WParam.ToInt32());
            return;
        }
        base.WndProc(ref m);
    }
}
