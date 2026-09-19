using XivDesktop.Core.Claude;

namespace XivDesktop.Core.Tests;

public class ClaudeNpcTests
{
    [Fact]
    public void TheSameSessionAlwaysSummonsTheSamePerson()
    {
        var a = NpcLooks.For("widget");
        var b = NpcLooks.For("widget");
        Assert.Equal(a.Seed, b.Seed);
        Assert.Equal(a.Race, b.Race);
        Assert.Equal(a.Customize, b.Customize);
        Assert.NotEqual(a.Seed, NpcLooks.For("docs").Seed);
    }

    [Fact]
    public void ARerollWithASeedIsRepeatableToo()
    {
        var first = NpcLooks.For("widget", seed: 12345);
        Assert.Equal(12345u, first.Seed);
        Assert.Equal(first.Customize, NpcLooks.For("widget", seed: 12345).Customize);
        Assert.NotEqual(first.Customize, NpcLooks.For("widget", seed: 12346).Customize);
    }

    [Fact]
    public void EveryRolledLookIsAWholeValidCharacter()
    {
        for (var i = 1u; i < 500; i++)
        {
            var look = NpcLooks.For("session-" + i);
            Assert.Equal(NpcLook.CustomizeLength, look.Customize.Length);
            Assert.InRange((int)look.Race, 1, 8);
            Assert.Equal((byte)look.Race, look.Customize[0]);
            Assert.InRange(look.Customize[1], (byte)0, (byte)1);     // gender
            Assert.NotEqual(0, look.Customize[5]);                   // a face, never "none"
            Assert.NotEqual(0, look.Customize[6]);                   // and hair
            Assert.Equal(5, look.Equipment.Count);
            Assert.NotEqual(0, look.Weapon);
            Assert.NotEqual("", look.Name);
        }
    }

    [Fact]
    public void TheNameplateReadsAsAClaude()
    {
        var look = NpcLooks.For("widget", name: "Quill");
        Assert.Equal("Claude — Quill", look.Nameplate);
        Assert.Contains("seed", look.Describe());
    }

    [Fact]
    public void TheModelsOwnAnswerIsUsedWhenItParses()
    {
        var fallback = NpcLooks.For("widget");
        var look = NpcLooks.ApplySelfDescription(fallback, """{"name":"Marginalia","race":"Elezen","gender":"feminine","style":"archivist"}""");
        Assert.Equal("Marginalia", look.Name);
        Assert.Equal(NpcRace.Elezen, look.Race);
        Assert.Equal((byte)NpcRace.Elezen, look.Customize[0]);
        Assert.Equal(1, look.Customize[1]);
        Assert.Equal(NpcStyle.Archivist, look.Style);
        Assert.Equal(fallback.Seed, look.Seed); // still repeatable: only the choices moved
    }

    [Fact]
    public void AChattyOrBrokenAnswerFallsBackToTheRoll()
    {
        var fallback = NpcLooks.For("widget");
        Assert.Equal(fallback.Customize, NpcLooks.ApplySelfDescription(fallback, null).Customize);
        Assert.Equal(fallback.Customize, NpcLooks.ApplySelfDescription(fallback, "I'd rather not say!").Customize);
        Assert.Equal(fallback.Customize, NpcLooks.ApplySelfDescription(fallback, "{not json}").Customize);

        // Fenced or prefixed JSON still parses.
        var fenced = NpcLooks.ApplySelfDescription(fallback, "Sure!\n```json\n{\"name\":\"Folio\"}\n```");
        Assert.Equal("Folio", fenced.Name);
    }

    [Fact]
    public void AnUnknownRaceOrStyleIsIgnoredRatherThanCrashing()
    {
        var fallback = NpcLooks.For("widget");
        var look = NpcLooks.ApplySelfDescription(fallback, """{"name":"X","race":"Dragon","style":"necromancer"}""");
        Assert.Equal(fallback.Race, look.Race);
        Assert.Equal(fallback.Style, look.Style);
    }

    [Fact]
    public void ANameFromTheModelIsCleanedBeforeItGoesOnANameplate()
    {
        var look = NpcLooks.ApplySelfDescription(NpcLooks.For("widget"), """{"name":"<script>alert(1)</script>"}""");
        Assert.DoesNotContain("<", look.Name);
        Assert.DoesNotContain(">", look.Name);
        Assert.True(look.Name.Length <= 20);
    }

    [Fact]
    public void TheNpcTypesWhileWorkingAndLooksUpWhenItNeedsADecision()
    {
        var director = new NpcDirector();
        Assert.Equal(NpcPose.Typing, director.Cue(SessionActivity.Working).Pose);
        Assert.Equal(NpcPose.Reading, director.Cue(SessionActivity.Working, ToolKind.Read).Pose);
        Assert.Equal(NpcPose.Reading, director.Cue(SessionActivity.Working, ToolKind.Search).Pose);

        var asking = director.Cue(SessionActivity.Asking);
        Assert.Equal(NpcPose.LookUp, asking.Pose);
        Assert.True(asking.OneShot);

        Assert.Equal(NpcPose.Nod, director.Cue(SessionActivity.Waiting).Pose);
        Assert.Equal(NpcPose.Shrug, director.Cue(SessionActivity.Broken).Pose);
        Assert.Equal(NpcPose.Idle, director.Cue(SessionActivity.Idle).Pose);
    }

    [Fact]
    public void AnUnchangedPoseIsNotReplayedAsAnEmoteEveryFrame()
    {
        var director = new NpcDirector();
        Assert.True(director.Cue(SessionActivity.Working).OneShot);
        Assert.False(director.Cue(SessionActivity.Working).OneShot);
        Assert.False(director.Cue(SessionActivity.Working).OneShot);
    }

    [Fact]
    public void WithNoSpawnerTheSeamIsSimplyUnavailable()
    {
        var avatar = NoNpcAvatar.Instance;
        Assert.False(avatar.Available);
        Assert.False(avatar.Summon("widget", NpcLooks.For("widget")));
        avatar.Play("widget", new NpcCue(NpcPose.Typing));
        avatar.Dismiss("widget");
    }
}
