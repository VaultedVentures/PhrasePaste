using System.Text;
using YamlDotNet.Serialization;

namespace PhrasePaste;

/// <summary>
/// Reads and writes phrases.yaml.
///
/// Reads go through YamlDotNet so hand edits (comments, odd indentation,
/// "<c>\n</c>" escapes) keep working. Writes use a small purpose-built emitter:
/// the schema is three keys, and the default serializer would turn every
/// multi-line phrase into a quoted string full of "\n", which is exactly the
/// unreadable thing the editor exists to remove. The emitter keeps names,
/// comments and multi-line text in block scalars so the file stays editable
/// by hand and by the app.
/// </summary>
internal static class PhraseStore
{
    private const string Header =
        "# PhrasePaste configuration\n" +
        "#\n" +
        "# Normally you never edit this file: right-click the tray icon and pick\n" +
        "# \"Edit phrases...\". Hand edits are still fine, the app reloads by itself\n" +
        "# a moment after you save.\n" +
        "#\n" +
        "# Hotkey syntax: Ctrl / Alt / Shift / Win joined with '+', then a key.\n" +
        "#   Letters, digits, F1-F24, Space, Enter, Tab, Backspace, Delete, Insert,\n" +
        "#   Home, End, PageUp, PageDown, Escape, arrows, Plus, Minus, Period, Comma,\n" +
        "#   Slash, Semicolon, Apostrophe, Backslash, [ , ] , Tilde, Num0-Num9.\n" +
        "#   Examples: \"Win+Alt+P\"  \"Ctrl+Alt+1\"  \"Ctrl+Shift+F8\"\n" +
        "# An empty hotkey means the phrase is only reachable from the picker\n" +
        "# and the tray menu, which is fine.\n";

    /// <summary>Parse phrases.yaml. Throws <see cref="YamlDotNet.Core.YamlException"/> on bad syntax.</summary>
    public static PhraseConfig Parse(string text)
    {
        var deserializer = new DeserializerBuilder().IgnoreUnmatchedProperties().Build();
        var loaded = deserializer.Deserialize<PhraseConfig>(text) ?? new PhraseConfig();
        loaded.Phrases ??= new List<Phrase>();
        loaded.Phrases = loaded.Phrases.Where(p => p is not null).ToList();

        foreach (var phrase in loaded.Phrases)
        {
            phrase.Name ??= "";
            phrase.Text ??= "";
        }

        loaded.PopupHotkey = string.IsNullOrWhiteSpace(loaded.PopupHotkey) ? PhraseConfig.DefaultPopupHotkey : loaded.PopupHotkey.Trim();
        loaded.CaptureHotkey = (loaded.CaptureHotkey ?? "").Trim();
        return loaded;
    }

    public static PhraseConfig Load(string path) => Parse(File.ReadAllText(path));

    /// <summary>Write the config, keeping one rolling backup of the previous contents.</summary>
    public static void Save(string path, PhraseConfig config)
    {
        var text = Serialize(config);

        if (File.Exists(path))
        {
            try
            {
                File.Copy(path, path + ".bak", overwrite: true);
            }
            catch
            {
                // a missing backup must never stop a save
            }
        }

        File.WriteAllText(path, text, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }

    public static string Serialize(PhraseConfig config)
    {
        var sb = new StringBuilder();
        sb.Append(Header);
        sb.Append('\n');
        sb.Append("popup_hotkey: ").Append(Quote(config.PopupHotkey)).Append('\n');
        sb.Append("capture_hotkey: ").Append(Quote(config.CaptureHotkey)).Append('\n');
        sb.Append('\n');
        sb.Append("# name  - what the picker and menus call this phrase\n");
        sb.Append("# hotkey - optional global hotkey, empty means picker/menu only\n");
        sb.Append("# text   - what gets pasted; use a blank line for a paragraph break\n");

        if (config.Phrases.Count == 0)
        {
            sb.Append("phrases: []\n");
            return sb.ToString();
        }

        sb.Append("phrases:\n");
        foreach (var phrase in config.Phrases)
        {
            sb.Append("  - name: ").Append(Quote(phrase.Name)).Append('\n');
            sb.Append("    hotkey: ").Append(Quote(phrase.Hotkey ?? "")).Append('\n');
            AppendText(sb, phrase.Text, "    ");
        }

        return sb.ToString();
    }

    /// <summary>Double-quoted single-line scalar. Newlines become \n escapes.</summary>
    private static string Quote(string? value)
    {
        var sb = new StringBuilder("\"");
        foreach (var ch in value ?? "")
        {
            switch (ch)
            {
                case '\\': sb.Append("\\\\"); break;
                case '"': sb.Append("\\\""); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                default: sb.Append(ch); break;
            }
        }
        return sb.Append('"').ToString();
    }

    /// <summary>
    /// Emit <c>text:</c> as a literal block scalar when it spans lines, so a
    /// signature stays readable in the file instead of becoming "a\nb\nc".
    /// Trailing newlines are preserved via the chomping indicator, and a line
    /// starting with whitespace forces the explicit indentation indicator.
    /// </summary>
    private static void AppendText(StringBuilder sb, string? text, string indent)
    {
        var value = (text ?? "").Replace("\r\n", "\n").Replace('\r', '\n');

        if (!value.Contains('\n') || value.Trim('\n').Length == 0)
        {
            sb.Append(indent).Append("text: ").Append(Quote(value)).Append('\n');
            return;
        }

        var trailing = 0;
        while (trailing < value.Length && value[value.Length - 1 - trailing] == '\n') trailing++;

        var core = value[..(value.Length - trailing)];
        var lines = core.Split('\n');

        var indicator = trailing switch { 0 => "|-", 1 => "|", _ => "|+" };

        // If the first non-empty line is itself indented, YAML would guess the
        // wrong block indent; the explicit indicator pins it to 2.
        var firstContent = lines.FirstOrDefault(l => l.Trim().Length > 0) ?? "";
        var needsIndentHint = firstContent.Length > 0 && char.IsWhiteSpace(firstContent[0]);

        var contentIndent = indent + "  ";
        sb.Append(indent).Append("text: ").Append(indicator).Append(needsIndentHint ? "2" : "").Append('\n');
        foreach (var line in lines)
        {
            if (line.Length == 0) sb.Append('\n');
            else sb.Append(contentIndent).Append(line).Append('\n');
        }

        // "|+" keeps every trailing blank line; one is implied by the block itself.
        for (var i = 1; i < trailing; i++) sb.Append('\n');
    }
}
