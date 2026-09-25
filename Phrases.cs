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

    public Phrase Clone() => new()
    {
        Name = Name,
        Text = Text,
        Hotkey = Hotkey,
    };

    /// <summary>
    /// What the user sees when there is no name: the first non-blank line of
    /// the text, trimmed to something that fits a list row.
    /// </summary>
    public string DisplayName()
    {
        if (!string.IsNullOrWhiteSpace(Name)) return Name.Trim();

        var line = (Text ?? "").Replace("\r", "").Split('\n')
            .FirstOrDefault(l => !string.IsNullOrWhiteSpace(l))?.Trim() ?? "";
        if (line.Length == 0) return "(empty phrase)";
        return line.Length > 40 ? line[..40] + "\u2026" : line;
    }

    /// <summary>First non-blank line of the text, for the picker's second row.</summary>
    public string Preview()
    {
        var lines = (Text ?? "").Replace("\r", "").Split('\n')
            .Select(l => l.Trim())
            .Where(l => l.Length > 0)
            .ToList();

        if (lines.Count == 0) return "(nothing to paste)";

        var preview = lines[0];
        if (preview.Length > 88) preview = preview[..88] + "\u2026";
        return lines.Count > 1 ? preview + "  \u2026" : preview;
    }
}

/// <summary>Root of phrases.yaml.</summary>
public sealed class PhraseConfig
{
    public const string DefaultPopupHotkey = "Win+Alt+P";

    [YamlMember(Alias = "popup_hotkey")]
    public string PopupHotkey { get; set; } = DefaultPopupHotkey;

    /// <summary>Optional global hotkey that grabs the current selection as a new phrase. Empty = tray menu only.</summary>
    [YamlMember(Alias = "capture_hotkey")]
    public string CaptureHotkey { get; set; } = "";

    [YamlMember(Alias = "phrases")]
    public List<Phrase> Phrases { get; set; } = new();

    /// <summary>Independent copy the editor can mutate without touching the live config.</summary>
    public PhraseConfig Clone() => new()
    {
        PopupHotkey = PopupHotkey,
        CaptureHotkey = CaptureHotkey,
        Phrases = Phrases.Select(p => p.Clone()).ToList(),
    };
}

internal static class Phrases
{
    /// <summary>
    /// First run: a couple of phrases that show what the app is for. Goes
    /// through the same emitter as a normal save, so the sample can never
    /// drift from the format the app writes.
    /// </summary>
    public static PhraseConfig SampleConfig() => new()
    {
        PopupHotkey = PhraseConfig.DefaultPopupHotkey,
        CaptureHotkey = "",
        Phrases = new List<Phrase>
        {
            new()
            {
                Name = "Example: quick reply",
                Text = "Sounds good, thanks!",
                Hotkey = "Ctrl+Alt+1",
            },
            new()
            {
                Name = "Example: email sign-off",
                Text = "Kind regards,\n\nScott Phillips\n",
                Hotkey = "Ctrl+Alt+2",
            },
            new()
            {
                Name = "Example: no hotkey (picker only)",
                Text = "Reachable from the picker and the tray menu.",
                Hotkey = "",
            },
        },
    };

    public static string SampleConfigText() => PhraseStore.Serialize(SampleConfig());
}
