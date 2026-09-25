using System.Text;
using System.Windows.Forms;

namespace PhrasePaste;

/// <summary>
/// Headless check that writing phrases.yaml and reading it back returns
/// exactly what went in. The emitter hand-rolls YAML block scalars, so this is
/// the guard against a signature quietly losing a line, a trailing newline or
/// its leading spaces. Run with <c>PhrasePaste.exe --selftest [outfile]</c>.
/// </summary>
internal static class SelfTest
{
    public static string Run()
    {
        var sb = new StringBuilder();
        var failures = 0;
        sb.AppendLine("PhrasePaste YAML round-trip self test");
        sb.AppendLine();

        var cases = new (string Label, string Text)[]
        {
            ("single line", "Sounds good, thanks!"),
            ("empty", ""),
            ("two lines", "Line one\nLine two"),
            ("trailing newline", "One trailing newline\n"),
            ("trailing three newlines", "Three trailing newlines\n\n\n"),
            ("leading spaces", "  first line is indented\nsecond line"),
            ("blank line inside", "para one\n\npara two"),
            ("quotes and backslashes", "He said \"hi\" from C:\\temp"),
            ("tab and unicode", "tab\there \u00e9\u00fc \u2713 \u2014"),
            ("crlf input", "one\r\ntwo\r\n"),
            ("only a newline", "\n"),
            ("signature", "Kind regards,\n\nScott Phillips\nCEO - Vaulted Ventures\n\nhttps://vaulted.ventures/\n"),
            ("dash and colon lines", "- looks like a list\nkey: looks like a mapping"),
            ("yaml hash and pipe", "# comment-ish\n| pipe-ish"),
        };

        foreach (var (label, text) in cases)
        {
            var config = One(text);
            var yaml = PhraseStore.Serialize(config);

            string? back;
            try
            {
                back = PhraseStore.Parse(yaml).Phrases[0].Text;
            }
            catch (Exception ex)
            {
                failures++;
                sb.AppendLine($"FAIL {label}: parsing what we wrote threw {ex.Message}");
                sb.AppendLine(Indented(yaml));
                continue;
            }

            var expected = Normalize(text);
            if (back != expected)
            {
                failures++;
                sb.AppendLine($"FAIL {label}: expected {Show(expected)} but got {Show(back)}");
                sb.AppendLine(Indented(yaml));
                continue;
            }

            sb.AppendLine($"ok   {label}");
        }

        // Several phrases at once, because the risky part of a block scalar is
        // the dedent back to the next list entry.
        var multi = new PhraseConfig
        {
            PopupHotkey = "Win+Alt+P",
            CaptureHotkey = "",
            Phrases = new List<Phrase>
            {
                new() { Name = "One", Text = "single", Hotkey = "Ctrl+Alt+1" },
                new() { Name = "Two", Text = "first\nsecond\n", Hotkey = "" },
                new() { Name = "Three", Text = "  indented\n\nlast", Hotkey = "Ctrl+Shift+K" },
            },
        };

        var multiBack = PhraseStore.Parse(PhraseStore.Serialize(multi));
        var multiOk = multiBack.Phrases.Count == 3
                      && multiBack.Phrases[0].Text == "single"
                      && multiBack.Phrases[0].Hotkey == "Ctrl+Alt+1"
                      && multiBack.Phrases[1].Text == "first\nsecond\n"
                      && multiBack.Phrases[1].Hotkey is null or ""
                      && multiBack.Phrases[2].Text == "  indented\n\nlast"
                      && multiBack.Phrases[2].Hotkey == "Ctrl+Shift+K";

        sb.AppendLine(multiOk ? "ok   three phrases in one file" : "FAIL three phrases in one file");
        if (!multiOk)
        {
            failures++;
            sb.AppendLine(Indented(PhraseStore.Serialize(multi)));
        }

        // Names, hotkeys and the two global hotkeys.
        var named = new PhraseConfig
        {
            PopupHotkey = "Ctrl+Alt+P",
            CaptureHotkey = "Win+Alt+S",
            Phrases = new List<Phrase> { new() { Name = "Quote \" & ampersand", Text = "x", Hotkey = "Ctrl+Shift+K" } },
        };
        var namedBack = PhraseStore.Parse(PhraseStore.Serialize(named));
        var namedOk = namedBack.PopupHotkey == "Ctrl+Alt+P"
                      && namedBack.CaptureHotkey == "Win+Alt+S"
                      && namedBack.Phrases.Count == 1
                      && namedBack.Phrases[0].Name == "Quote \" & ampersand"
                      && namedBack.Phrases[0].Hotkey == "Ctrl+Shift+K";

        sb.AppendLine(namedOk ? "ok   names, hotkeys and global hotkeys" : "FAIL names, hotkeys and global hotkeys");
        if (!namedOk) failures++;

        // Every combination the capture box can produce has to survive the
        // parser, or the editor could store something the app cannot read.
        var keys = new[]
        {
            Keys.A, Keys.Z, Keys.D0, Keys.D9, Keys.F1, Keys.F24, Keys.Space, Keys.Enter, Keys.Tab,
            Keys.Back, Keys.Delete, Keys.Insert, Keys.Home, Keys.End, Keys.Prior, Keys.Next, Keys.Escape,
            Keys.Up, Keys.Down, Keys.Left, Keys.Right, Keys.Oemplus, Keys.OemMinus, Keys.OemPeriod,
            Keys.Oemcomma, Keys.OemQuestion, Keys.Oem1, Keys.Oem7, Keys.Oem5, Keys.Oem4, Keys.Oem6,
            Keys.Oem3, Keys.NumPad0, Keys.NumPad9,
        };

        var badKeys = new List<string>();
        foreach (var key in keys)
        {
            var formatted = HotkeyManager.Format(key, ctrl: true, alt: true, shift: true, win: true);
            if (formatted is null || !HotkeyManager.TryParse(formatted, out _, out _))
            {
                badKeys.Add($"{key} -> {formatted ?? "<no name>"}");
            }
        }

        if (badKeys.Count > 0)
        {
            failures++;
            sb.AppendLine($"FAIL captured keys that will not parse: {string.Join(", ", badKeys)}");
        }
        else
        {
            sb.AppendLine($"ok   all {keys.Length} capturable keys round-trip through the parser");
        }

        sb.AppendLine();
        sb.AppendLine(failures == 0 ? "RESULT: PASS" : $"RESULT: FAIL ({failures})");

        AppendLiveConfig(sb, ref failures);
        return sb.ToString();
    }

    /// <summary>
    /// Re-saving the config that is actually installed must not change what it
    /// means. Also prints the file the editor would write, so the format can be
    /// eyeballed rather than guessed at.
    /// </summary>
    private static void AppendLiveConfig(StringBuilder sb, ref int failures)
    {
        var livePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "PhrasePaste",
            "phrases.yaml");

        sb.AppendLine();
        if (!File.Exists(livePath))
        {
            sb.AppendLine($"no live config at {livePath}");
            return;
        }

        try
        {
            var original = PhraseStore.Parse(File.ReadAllText(livePath));
            var rewritten = PhraseStore.Serialize(original);
            var reparsed = PhraseStore.Parse(rewritten);

            var same = original.PopupHotkey == reparsed.PopupHotkey
                       && original.CaptureHotkey == reparsed.CaptureHotkey
                       && original.Phrases.Count == reparsed.Phrases.Count;

            if (same)
            {
                for (var i = 0; i < original.Phrases.Count; i++)
                {
                    if (original.Phrases[i].Name == reparsed.Phrases[i].Name
                        && original.Phrases[i].Text == reparsed.Phrases[i].Text
                        && (original.Phrases[i].Hotkey ?? "") == (reparsed.Phrases[i].Hotkey ?? ""))
                    {
                        continue;
                    }

                    same = false;
                    sb.AppendLine($"  differs at phrase {i}: \"{original.Phrases[i].Name}\"");
                }
            }

            sb.AppendLine(same
                ? $"ok   live config survives a re-save ({original.Phrases.Count} phrases)"
                : "FAIL live config changes meaning when re-saved");

            if (!same) failures++;

            sb.AppendLine();
            sb.AppendLine($"---- {livePath} as the editor would write it ----");
            sb.AppendLine(rewritten);
        }
        catch (Exception ex)
        {
            failures++;
            sb.AppendLine($"FAIL live config could not be re-read: {ex.Message}");
        }
    }

    private static PhraseConfig One(string text) => new()
    {
        PopupHotkey = "Win+Alt+P",
        CaptureHotkey = "",
        Phrases = new List<Phrase> { new() { Name = "Case", Text = text, Hotkey = "Ctrl+Alt+1" } },
    };

    private static string Normalize(string text) => text.Replace("\r\n", "\n").Replace('\r', '\n');

    private static string Show(string text) => "\"" + text.Replace("\n", "\\n").Replace("\t", "\\t") + "\"";

    private static string Indented(string block)
    {
        var sb = new StringBuilder();
        foreach (var line in block.Split('\n')) sb.Append("      | ").Append(line).Append('\n');
        return sb.ToString();
    }
}
