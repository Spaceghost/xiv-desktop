namespace XivDesktop.Core.Input;

/// <summary>
/// Decides whether a modifier is really held, for modifiers whose raw "down" can go stale. Call
/// <see cref="Update"/> once per frame with the raw key-down query, before matching chords, and answer the
/// guarded modifiers (<see cref="Guards"/>) from <see cref="IsHeld"/>.
/// <para>
/// A guarded modifier counts as held only after its press was seen: up in one sampled frame, down in a later
/// one, with the game in the foreground throughout. Losing the foreground forgets every modifier, and one
/// that is already down when sampling starts again stays not-held until it has been seen up. Super also stops
/// counting after <see cref="SuperMaxHoldMs"/> of continuous hold, and needs a fresh press after that.
/// </para>
/// <para>
/// Why: under Wine on GNOME, the shell takes Super for the overview. Wine sees the press but not the release,
/// and Wine's resync on focus-in covers Shift/Ctrl/Alt but not the Windows keys, so GetAsyncKeyState keeps
/// VK_LWIN down until the next real Super press and release. Without this, every plain <c>d</c> in that time
/// matched Super+D. A stale modifier now costs at most one missed chord; it never turns a bare key into one.
/// </para>
/// </summary>
public sealed class ModifierGate
{
    /// <summary>The longest continuous hold for which Super still counts (a backstop if a focus loss is missed).</summary>
    public const long DefaultSuperMaxHoldMs = 10_000;

    private static readonly int[] Keys =
    [
        KeyChord.VkShift, KeyChord.VkLShift, KeyChord.VkRShift,
        KeyChord.VkControl, KeyChord.VkLControl, KeyChord.VkRControl,
        KeyChord.VkMenu, KeyChord.VkLMenu, KeyChord.VkRMenu,
        KeyChord.VkLWin, KeyChord.VkRWin,
    ];

    private enum State : byte
    {
        Unknown, // not seen up since sampling (re)started or since an expired hold: a raw "down" is not trusted
        Up,
        Down, // the press was seen: held
    }

    private readonly State[] state = new State[Keys.Length];
    private readonly long[] downSince = new long[Keys.Length];

    public long SuperMaxHoldMs { get; init; } = DefaultSuperMaxHoldMs;

    public static bool IsModifier(int vk) => Index(vk) >= 0;

    public static bool IsSuper(int vk) => vk is KeyChord.VkLWin or KeyChord.VkRWin;

    /// <summary>
    /// Whether a key is answered by the gate. Super always (Wine can leave it stuck whatever it is read from);
    /// Shift/Ctrl/Alt when they are read with GetAsyncKeyState rather than the game's own key state.
    /// </summary>
    public static bool Guards(int vk, bool readAsync) => IsSuper(vk) || (readAsync && IsModifier(vk));

    /// <summary>Samples every modifier once. <paramref name="foreground"/> false forgets them all.</summary>
    public void Update(Func<int, bool> rawDown, bool foreground, long nowMs)
    {
        if (!foreground)
        {
            Reset();
            return;
        }

        for (var i = 0; i < Keys.Length; i++)
        {
            var down = rawDown(Keys[i]);
            switch (state[i])
            {
                case State.Unknown:
                    if (!down)
                        state[i] = State.Up;
                    break;
                case State.Up:
                    if (down)
                    {
                        state[i] = State.Down;
                        downSince[i] = nowMs;
                    }

                    break;
                case State.Down:
                    if (!down)
                        state[i] = State.Up;
                    else if (IsSuper(Keys[i]) && nowMs - downSince[i] > SuperMaxHoldMs)
                        state[i] = State.Unknown;
                    break;
            }
        }
    }

    /// <summary>Held as of the last <see cref="Update"/>: the press was seen and nothing since has made it doubtful.</summary>
    public bool IsHeld(int vk)
    {
        var i = Index(vk);
        return i >= 0 && state[i] == State.Down;
    }

    /// <summary>Forget every modifier: each must be seen up, then down, before it counts again.</summary>
    public void Reset() => Array.Clear(state);

    private static int Index(int vk) => Array.IndexOf(Keys, vk);
}
