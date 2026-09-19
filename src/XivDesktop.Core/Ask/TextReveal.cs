namespace XivDesktop.Core.Ask;

/// <summary>
/// Typewriter pacing for the dialogue box, like the game's Talk window: characters appear at a steady
/// rate with short holds after punctuation. The target text may keep growing while an answer streams;
/// the reveal never runs ahead of it. Frame-rate independent: advance it with the frame's delta time.
/// </summary>
public sealed class TextReveal
{
    private double budget; // seconds of reveal time banked but not yet spent

    /// <param name="charsPerSecond">Reveal rate; 0 or less shows everything at once.</param>
    public TextReveal(double charsPerSecond = 45)
    {
        CharsPerSecond = charsPerSecond;
    }

    public double CharsPerSecond { get; set; }

    /// <summary>Extra hold after a sentence end (. ! ? …), in seconds.</summary>
    public double SentencePause { get; set; } = 0.22;

    /// <summary>Extra hold after a clause break (, ; :), in seconds.</summary>
    public double ClausePause { get; set; } = 0.08;

    /// <summary>Characters of the target currently shown.</summary>
    public int Visible { get; private set; }

    /// <summary>Everything in <paramref name="target"/> is shown.</summary>
    public bool Caught(string target) => Visible >= target.Length;

    public void Reset()
    {
        Visible = 0;
        budget = 0;
    }

    /// <summary>Shows everything at once (the player clicked or pressed a key to skip).</summary>
    public void Skip(string target)
    {
        Visible = target.Length;
        budget = 0;
    }

    /// <summary>Advances by <paramref name="dt"/> seconds against the current target; returns <see cref="Visible"/>.</summary>
    public int Advance(double dt, string target)
    {
        if (Visible > target.Length)
            Visible = target.Length; // the target was replaced by something shorter
        if (CharsPerSecond <= 0)
        {
            Visible = target.Length;
            return Visible;
        }

        if (Visible >= target.Length)
        {
            budget = 0; // waiting for more text: do not bank time and burst later
            return Visible;
        }

        budget += Math.Clamp(dt, 0, 0.25);
        var perChar = 1.0 / CharsPerSecond;
        while (Visible < target.Length)
        {
            var cost = perChar + PauseAfter(target, Visible - 1);
            if (budget < cost)
                break;
            budget -= cost;
            Visible++;
            // Whitespace is free: it would only look like a stutter.
            while (Visible < target.Length && target[Visible] == ' ')
                Visible++;
        }

        return Visible;
    }

    /// <summary>The hold before showing the character after index <paramref name="i"/>.</summary>
    public double PauseAfter(string text, int i)
    {
        if (i < 0 || i >= text.Length)
            return 0;
        var next = i + 1 < text.Length ? text[i + 1] : ' ';
        if (!char.IsWhiteSpace(next))
            return 0; // "3.5", "e.g." mid-word
        return text[i] switch
        {
            '.' or '!' or '?' or '…' => SentencePause,
            ',' or ';' or ':' => ClausePause,
            _ => 0,
        };
    }

    /// <summary>Settings value (1 slow … 5 instant) → characters per second.</summary>
    public static double RateFor(int speed) => speed switch
    {
        <= 1 => 20,
        2 => 32,
        3 => 45,
        4 => 70,
        _ => 0,
    };
}
