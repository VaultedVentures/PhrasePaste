using YamlDotNet.Serialization;

namespace PhrasePaste;

/// <summary>One pasteable phrase from the config file.</summary>
public sealed class Phrase
{
    [YamlMember(Alias = "name")]
    public string Name { get; set; } = "";

    [YamlMember(Alias = "text")]
    public string Text { get; set; } = "";

    /// <summary>Optional global hotkey, e.g. "Ctrl+Alt+1". Empty/absent = picker and tray menu only.</summary>
    [YamlMember(Alias = "hotkey")]
    public string? Hotkey { get; set; }
}

/// <summary>Root of phrases.yaml.</summary>
public sealed class PhraseConfig
{
    [YamlMember(Alias = "popup_hotkey")]
    public string PopupHotkey { get; set; } = "Win+Alt+P";

    [YamlMember(Alias = "phrases")]
    public List<Phrase> Phrases { get; set; } = new();
}

internal static class Phrases
{
    /// <summary>Written to %APPDATA%\PhrasePaste\phrases.yaml on first run.</summary>
    public static string SampleConfigText => """
        # PhrasePaste configuration
        #
        # Edit this file, then use the tray menu -> Reload phrases (no restart needed).
        #
        # Hotkey syntax: one or more of Ctrl / Alt / Shift / Win, joined with '+', then a key.
        #   Letters, digits, F1-F24, Space, Enter, Tab, Backspace, Delete, Insert, Home, End,
        #   PageUp, PageDown, Escape, arrows, Plus, Minus, Period, Comma, Slash, Semicolon...
        # Examples: "Win+Alt+P"  "Ctrl+Alt+1"  "Ctrl+Shift+F8"

        # Global hotkey that opens the searchable phrase picker.
        popup_hotkey: "Win+Alt+P"

        # Each phrase: name (shown in menus), text (what gets pasted), hotkey (optional).
        # Phrases without a hotkey are still reachable via the picker and the tray menu.
        phrases:
          - name: "Sample: short reply"
            text: "Sounds good, thanks!"
            hotkey: "Ctrl+Alt+1"

          - name: "Sample: multi-line"
            text: |
              Line one
              Line two
              Line three
            hotkey: "Ctrl+Alt+2"

          - name: "Sample: SQL select"
            text: "SELECT * FROM users WHERE id = 1;"
            hotkey: "Ctrl+Alt+3"

          - name: "Sample: email greeting"
            text: "Hi,\n\nJust following up on this.\n\nCheers,"
            hotkey: "Ctrl+Alt+4"

          - name: "Sample: no hotkey"
            text: "This one is only in the picker and tray menu."
            hotkey: ""
        """;
}
