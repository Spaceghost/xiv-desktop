using XivDesktop.Core.Input;

namespace XivDesktop.Core.Tests;

/// <summary>
/// The stale-Super bug: under Wine on GNOME, GetAsyncKeyState can report VK_LWIN down long after it was
/// released, and a plain D then matched Super+D. These drive <see cref="ModifierGate"/> and
/// <see cref="ChordTracker"/> together the way KeybindService does, one frame at a time.
/// </summary>
public class ModifierGateTests
{
    private const int D = 'D';

    /// <summary>One framework tick: sample the gate, then match chords with Super answered by the gate.</summary>
    private sealed class Rig
    {
        public readonly ModifierGate Gate = new();
        public readonly ChordTracker Tracker = new() { Bindings = Keybinds.Compile(Keybinds.Defaults()) };
        public long Now;

        public List<string> Frame(bool foreground, params int[] rawDown)
        {
            Now += 16;
            bool Raw(int vk) => rawDown.Contains(vk);
            if (!foreground)
            {
                Tracker.Reset();
                Gate.Reset();
                return [];
            }

            Gate.Update(Raw, foreground: true, Now);
            return Tracker.Update(vk => ModifierGate.Guards(vk, readAsync: true) ? Gate.IsHeld(vk) : Raw(vk), blocked: false).Fired;
        }
    }

    [Fact]
    public void AGenuineSuperDStillFires()
    {
        var r = new Rig();
        Assert.Empty(r.Frame(true));
        Assert.Empty(r.Frame(true, KeyChord.VkLWin));
        Assert.Equal([KeyAction.Launcher], r.Frame(true, KeyChord.VkLWin, D));
        Assert.Empty(r.Frame(true, KeyChord.VkLWin, D)); // held: not repeated
        Assert.Empty(r.Frame(true));
    }

    [Fact]
    public void SuperAndDInTheSameFrameStillFire()
    {
        var r = new Rig();
        r.Frame(true);
        Assert.Equal([KeyAction.Launcher], r.Frame(true, KeyChord.VkLWin, D));
    }

    [Fact]
    public void ASuperStuckAfterAFocusLossNeverTurnsDIntoSuperD()
    {
        var r = new Rig();
        r.Frame(true);
        r.Frame(true, KeyChord.VkLWin); // Super pressed in game; GNOME takes the release and opens the overview
        r.Frame(false, KeyChord.VkLWin);
        r.Frame(false, KeyChord.VkLWin);

        // Back in game, Wine still reports Super down: plain D, many times, is plain D.
        for (var i = 0; i < 5; i++)
        {
            Assert.Empty(r.Frame(true, KeyChord.VkLWin, D));
            Assert.Empty(r.Frame(true, KeyChord.VkLWin));
        }

        Assert.False(r.Gate.IsHeld(KeyChord.VkLWin));
    }

    [Fact]
    public void AStuckSuperFromBeforeSamplingStartedDoesNotCount()
    {
        var r = new Rig();
        Assert.Empty(r.Frame(true, KeyChord.VkRWin, D));
        Assert.Empty(r.Frame(true, KeyChord.VkRWin));
        Assert.Empty(r.Frame(true, KeyChord.VkRWin, D));
    }

    [Fact]
    public void ARealSuperPressAfterAStuckOneWorksAgain()
    {
        var r = new Rig();
        r.Frame(false);
        r.Frame(true, KeyChord.VkLWin); // stuck
        Assert.Empty(r.Frame(true, KeyChord.VkLWin, D));
        r.Frame(true); // the release of a real press resyncs Wine
        r.Frame(true, KeyChord.VkLWin);
        Assert.Equal([KeyAction.Launcher], r.Frame(true, KeyChord.VkLWin, D));
    }

    [Fact]
    public void SuperStopsCountingAfterTheMaximumHold()
    {
        var r = new Rig();
        r.Frame(true);
        r.Frame(true, KeyChord.VkLWin); // pressed; the release is lost and no focus loss is seen
        r.Now += ModifierGate.DefaultSuperMaxHoldMs;
        r.Frame(true, KeyChord.VkLWin);
        Assert.Empty(r.Frame(true, KeyChord.VkLWin, D));
        Assert.False(r.Gate.IsHeld(KeyChord.VkLWin));
    }

    [Fact]
    public void SuperHeldAcrossSeveralChordsWithinTheMaximumHoldKeepsWorking()
    {
        var r = new Rig();
        r.Frame(true);
        r.Frame(true, KeyChord.VkLWin);
        Assert.Equal([KeyAction.Workspace(1)], r.Frame(true, KeyChord.VkLWin, '1'));
        r.Frame(true, KeyChord.VkLWin);
        r.Now += 3_000;
        Assert.Equal([KeyAction.Workspace(2)], r.Frame(true, KeyChord.VkLWin, '2'));
    }

    [Fact]
    public void ShiftReadAsynchronouslyIsGatedTheSameWay()
    {
        var gate = new ModifierGate();
        gate.Update(vk => vk == KeyChord.VkLShift, foreground: true, 0);
        Assert.False(gate.IsHeld(KeyChord.VkLShift)); // down before it was seen up
        gate.Update(_ => false, foreground: true, 16);
        gate.Update(vk => vk == KeyChord.VkLShift, foreground: true, 32);
        Assert.True(gate.IsHeld(KeyChord.VkLShift));
        gate.Update(vk => vk == KeyChord.VkLShift, foreground: true, 32 + ModifierGate.DefaultSuperMaxHoldMs * 3);
        Assert.True(gate.IsHeld(KeyChord.VkLShift)); // the hold limit is for Super only
        gate.Update(vk => vk == KeyChord.VkLShift, foreground: false, 64);
        Assert.False(gate.IsHeld(KeyChord.VkLShift));
    }

    [Theory]
    [InlineData(KeyChord.VkLWin, false, true)]
    [InlineData(KeyChord.VkRWin, false, true)]
    [InlineData(KeyChord.VkLWin, true, true)]
    [InlineData(KeyChord.VkShift, false, false)]
    [InlineData(KeyChord.VkShift, true, true)]
    [InlineData(KeyChord.VkRMenu, true, true)]
    [InlineData(KeyChord.VkLControl, false, false)]
    [InlineData('D', true, false)]
    [InlineData('D', false, false)]
    public void GuardsSuperAlwaysAndOtherModifiersOnlyWhenReadAsynchronously(int vk, bool readAsync, bool guarded)
        => Assert.Equal(guarded, ModifierGate.Guards(vk, readAsync));

    [Fact]
    public void TheGameKeyStateForShiftIsTrustedWhenNotReadAsynchronously()
    {
        var r = new Rig();
        var gate = r.Gate;
        bool Raw(int vk) => vk is KeyChord.VkShift or KeyChord.VkLWin or '2';
        gate.Update(vk => vk != KeyChord.VkLWin && Raw(vk), true, 0); // Shift already down: never seen pressed
        gate.Update(Raw, true, 16);
        Assert.False(gate.IsHeld(KeyChord.VkShift));
        var fired = r.Tracker.Update(vk => ModifierGate.Guards(vk, readAsync: false) ? gate.IsHeld(vk) : Raw(vk), blocked: false).Fired;
        Assert.Equal([KeyAction.MoveTo(2)], fired);
    }
}
