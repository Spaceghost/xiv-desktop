using System.Text;
using XivDesktop.Core.Input;
using XivDesktop.Core.Windows;

namespace XivDesktop.Core.Tests;

/// <summary>
/// The parsers that read text this plugin did not write (ghostty's IPC replies, .desktop files, key chords)
/// must return "nothing" on bad input, never throw. XIVDESKTOP_FUZZ_SECONDS sets the budget (default 2, the
/// nightly workflow raises it); XIVDESKTOP_FUZZ_SEED replays a run. A failure names its seed and input.
/// </summary>
public sealed class FuzzTests
{
    private static readonly string[] Corpus =
    [
        """{"ok":true,"result":{"windows":[{"id":1,"title":"a","app":"b","x":0,"y":0,"w":10,"h":10,"focused":true}]}}""",
        """{"ok":false,"error":{"code":-1,"message":"no"}}""",
        """{"ok":true,"result":{"queued":12}}""",
        """{"apps":[{"id":"org.x","name":"X","exec":"x %U","icon":"x"}]}""",
        """{"panel":3,"kind":"window","title":"t"}""",
        """{"connected":true,"version":"3","windows":"wayland"}""",
        "[Desktop Entry]\nType=Application\nName=X\nName[de]=Y\nExec=x --flag \"a b\" %U\nIcon=x\nTerminal=false\nCategories=A;B;\n",
        "ctrl+shift+grave",
        "alt+f4",
    ];

    private static readonly string[] Splices = ["null", "{}", "[]", "\"\"", "-1", "1e999", "99999999999999999999", "\"\\ud800\"", "true", "\u0000", "}", "[", "\"", ",", "\n[", "=", "+", "%", "\\"];

    [Fact]
    public void ParsersNeverThrow()
    {
        var seed = int.TryParse(Environment.GetEnvironmentVariable("XIVDESKTOP_FUZZ_SEED"), out var s) ? s : Environment.TickCount;
        var seconds = double.TryParse(Environment.GetEnvironmentVariable("XIVDESKTOP_FUZZ_SECONDS"), out var b) ? b : 2;
        var rng = new Random(seed);
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        while (DateTime.UtcNow < deadline)
        {
            var text = Mutate(rng);
            try
            {
                GhosttyWire.ParseAgentApps(text);
                GhosttyWire.ParseFocus(text);
                var reply = GhosttyWire.ParseReply(text);
                GhosttyWire.ParseQueued(reply);
                GhosttyWire.ParseWindowList(reply);
                GhosttyWire.ParseAgentStatus(text);
                Panels.ParseList(text);
                Palette.ArcadeLink.Parse(text);
                DesktopEntryParser.Parse(text, "fuzz");
                KeyChord.TryParse(text, out _);
            }
            catch (Exception ex)
            {
                Assert.Fail($"seed {seed}: {ex.GetType().Name} for {Convert.ToBase64String(Encoding.UTF8.GetBytes(text))}: {ex.Message}");
            }
        }
    }

    private static string Mutate(Random rng)
    {
        var text = new StringBuilder(Corpus[rng.Next(Corpus.Length)]);
        for (var i = rng.Next(5); i > 0 && text.Length > 0; i--)
        {
            var at = rng.Next(text.Length);
            switch (rng.Next(5))
            {
                case 0: text.Remove(at, Math.Min(rng.Next(1, 12), text.Length - at)); break;
                case 1: text.Insert(at, Splices[rng.Next(Splices.Length)]); break;
                case 2: text[at] = (char)rng.Next(1, 0x250); break;
                case 3: text.Insert(at, text.ToString(at, Math.Min(rng.Next(1, 40), text.Length - at))); break;
                default: text.Length = at; break;
            }
        }

        return text.ToString();
    }
}
