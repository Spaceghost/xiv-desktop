using XivDesktop.Core.Input;

namespace XivDesktop.Core.Tests;

/// <summary>
/// Allocation budgets for code that runs every frame or every keystroke. Bytes per call on one thread,
/// from the runtime's own counter; each budget is about twice today's cost, so a change that doubles the
/// garbage fails here instead of as stutter in the game.
/// </summary>
public sealed class AllocationBudgetTests
{
    private static long PerOperation(Action action, int n = 5000)
    {
        for (var i = 0; i < 500; i++)
            action();
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < n; i++)
            action();
        return (GC.GetAllocatedBytesForCurrentThread() - before) / n;
    }

    [Fact]
    public void ParsingAKeyChordStaysWithinItsAllocationBudget()
    {
        var bytes = PerOperation(() => KeyChord.TryParse("ctrl+shift+grave", out _));
        Assert.True(bytes < 320, $"{bytes} bytes allocated per parse, budget 320");
    }
}
