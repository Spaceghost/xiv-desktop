using XivDesktop.Core.Windows;
using XivDesktop.Core.Workspaces;

namespace XivDesktop.Core.Tests;

public class WorkspaceTests
{
    private static WindowPanel W(long id, string title = "", string app = "", string state = "live", string kind = "pet")
        => new() { Id = id, Title = title, App = app, State = state, Kind = kind };

    [Fact]
    public void NewPanelsJoinTheCurrentWorkspace()
    {
        var m = new WorkspaceModel();
        Assert.True(m.Reconcile([W(1, "foot")]));
        Assert.Equal(1, m.WorkspaceOf(1));
        m.Switch(3);
        m.Reconcile([W(1, "foot"), W(2, "Firefox")]);
        Assert.Equal(3, m.WorkspaceOf(2));
        Assert.Equal(0, m.WorkspaceOf(99));
        Assert.False(m.Reconcile([W(1, "foot"), W(2, "Firefox")]));
    }

    [Fact]
    public void SwitchingPlansHidesAndShows()
    {
        var m = new WorkspaceModel();
        var ws = new[] { W(1, "a"), W(2, "b", kind: "pin") };
        m.Reconcile(ws);
        m.Move(2, 2);
        Assert.Equal([new VisibilityChange(2, true)], m.Plan(ws));
        m.MarkHidden(2, true);
        Assert.Empty(m.Plan(ws));

        Assert.True(m.Switch(2));
        var plan = m.Plan(ws);
        Assert.Contains(new VisibilityChange(1, true), plan);
        Assert.Contains(new VisibilityChange(2, false), plan);
        Assert.False(m.Switch(2));
        Assert.False(m.Switch(10));
        Assert.Equal([2L], m.PanelsOn(2, ws));
    }

    [Fact]
    public void FullScreenTabsAndEndedPanelsAreLeftAlone()
    {
        var m = new WorkspaceModel();
        var ws = new[] { W(1, "a", kind: "full"), W(2, "b", kind: "tab"), W(3, "c", state: "ended") };
        m.Reconcile(ws);
        m.Switch(5);
        Assert.Empty(m.Plan(ws));
    }

    [Fact]
    public void EntriesRebindByTitleThenAppAfterAReconnect()
    {
        var m = new WorkspaceModel();
        m.Reconcile([W(1, "notes.txt - Text Editor", "gnome-text-editor"), W(2, "btop", "foot")]);
        m.Move(1, 4);
        m.Move(2, 6);
        var saved = m.Export();

        // ghostty reloaded: new panel ids, same windows.
        var after = new WorkspaceModel(saved, current: 1);
        after.Reconcile([W(10, "btop", "foot"), W(11, "other.txt - Text Editor", "gnome-text-editor")]);
        Assert.Equal(6, after.WorkspaceOf(10));
        Assert.Equal(4, after.WorkspaceOf(11)); // same app, same " - Text Editor" stem

        // An unrelated window is new: current workspace.
        after.Reconcile([W(10, "btop", "foot"), W(11, "other.txt - Text Editor", "gnome-text-editor"), W(12, "Calculator", "gnome-calculator")]);
        Assert.Equal(1, after.WorkspaceOf(12));
    }

    [Fact]
    public void EndedPanelsBecomeOrphansAndAreCapped()
    {
        var m = new WorkspaceModel();
        m.Reconcile([W(1, "a")]);
        m.Reconcile([W(1, "a", state: "ended")]);
        Assert.Equal(0, m.WorkspaceOf(1));
        Assert.Equal(0, Assert.Single(m.Export()).PanelId);

        for (var i = 100; i < 100 + WorkspaceModel.MaxOrphans + 10; i++)
        {
            m.Reconcile([W(i, "t" + i)]);
            m.Reconcile([]);
        }

        Assert.True(m.Export().Count <= WorkspaceModel.MaxOrphans);
    }

    [Fact]
    public void HiddenStateIsForgottenForGonePanels()
    {
        var m = new WorkspaceModel();
        m.Reconcile([W(1, "a")]);
        m.MarkHidden(1, true);
        m.Reconcile([]);
        Assert.Empty(m.Hidden);
    }

    [Fact]
    public void LoadClampsBadConfig()
    {
        var m = new WorkspaceModel([new WorkspaceEntry { Workspace = 42, PanelId = 1, Title = null! }], current: -3);
        Assert.Equal(1, m.Current);
        Assert.Equal(WorkspaceModel.Count, m.WorkspaceOf(1));
    }
}
