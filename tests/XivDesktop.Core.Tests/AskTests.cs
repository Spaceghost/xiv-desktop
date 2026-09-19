using System.Text;
using System.Text.Json;
using XivDesktop.Core.Ask;

namespace XivDesktop.Core.Tests;

public class ChatStreamTests
{
    private const string Chunk = "data: {\"id\":\"c\",\"object\":\"chat.completion.chunk\",\"choices\":[{\"index\":0,\"delta\":{\"content\":\"%\"},\"finish_reason\":null}]}\n\n";

    private static string C(string s) => Chunk.Replace("%", s);

    [Fact]
    public void ParsesOllamaChunksAndDone()
    {
        var p = new ChatStreamParser();
        var events = p.Feed(C("Hello") + C("!") + "data: {\"choices\":[{\"index\":0,\"delta\":{},\"finish_reason\":\"stop\"}]}\n\ndata: [DONE]\n\n").ToList();
        Assert.Equal(["Hello", "!"], events.Where(e => e.Kind == StreamEventKind.Delta).Select(e => e.Text));
        Assert.Equal(StreamEventKind.Done, events[^1].Kind);
        Assert.True(p.Finished);
        Assert.Empty(p.Feed(C("late")));
    }

    [Fact]
    public void SurvivesAnyByteSplitIncludingInsideUtf8()
    {
        var body = Encoding.UTF8.GetBytes(C("Café ▼ ok") + "data: [DONE]\n\n");
        for (var split = 1; split < body.Length; split += 7)
        {
            var p = new ChatStreamParser();
            var text = new StringBuilder();
            foreach (var e in p.Feed(body.AsSpan(0, split)).Concat(p.Feed(body.AsSpan(split))))
            {
                if (e.Kind == StreamEventKind.Delta)
                    text.Append(e.Text);
            }

            Assert.Equal("Café ▼ ok", text.ToString());
            Assert.True(p.Finished);
        }
    }

    [Fact]
    public void HandlesCrlfCommentsAndMissingFinalBlankLine()
    {
        var p = new ChatStreamParser();
        var events = p.Feed(": keep-alive\r\n\r\n" + C("a").Replace("\n", "\r\n")).Concat(p.Feed("data: " + "{\"choices\":[{\"delta\":{\"content\":\"b\"}}]}")).Concat(p.Complete()).ToList();
        Assert.Equal("ab", string.Concat(events.Where(e => e.Kind == StreamEventKind.Delta).Select(e => e.Text)));
        Assert.Equal(StreamEventKind.Done, events[^1].Kind);
    }

    [Fact]
    public void ReportsErrorsAndIgnoresReasoningAndGarbage()
    {
        var p = new ChatStreamParser();
        var events = p.Feed("data: {\"choices\":[{\"delta\":{\"reasoning\":\"hmm\"}}]}\n\ndata: not json\n\ndata: {\"error\":{\"message\":\"model not allowed\"}}\n\n").ToList();
        Assert.Single(events);
        Assert.Equal(StreamEvent.Error("model not allowed"), events[0]);
    }

    [Fact]
    public void ParsesAnthropicStream()
    {
        var p = new ChatStreamParser();
        var events = p.Feed("event: content_block_delta\ndata: {\"type\":\"content_block_delta\",\"delta\":{\"type\":\"text_delta\",\"text\":\"Hi\"}}\n\nevent: message_stop\ndata: {\"type\":\"message_stop\"}\n\n").ToList();
        Assert.Equal([StreamEvent.Delta("Hi"), StreamEvent.Done()], events);
    }
}

public class CueTests
{
    [Fact]
    public void StripsTagsAndReportsCues()
    {
        var f = new CueFilter();
        var (text, cues) = f.Push("[emote:laugh] [player:nod] Ha, that is a good one.");
        var (rest, more) = f.Flush();
        Assert.Equal(" Ha, that is a good one.", text + rest);
        Assert.Equal([new Cue(CueTarget.Npc, "laugh"), new Cue(CueTarget.Player, "yes")], cues.Concat(more));
    }

    [Fact]
    public void HoldsATagSplitAcrossDeltas()
    {
        var f = new CueFilter();
        var out1 = f.Push("Well [emo");
        var out2 = f.Push("te:thi");
        var out3 = f.Push("nk] then");
        var end = f.Flush();
        Assert.Equal("Well ", out1.Text);
        Assert.Equal("", out2.Text);
        Assert.Equal([new Cue(CueTarget.Npc, "think")], out3.Cues);
        Assert.Equal("Well  then".Replace("  ", " "), out1.Text + out2.Text + out3.Text + end.Text);
    }

    [Fact]
    public void LeavesOtherBracketsAndUnknownEmotesAsText()
    {
        var f = new CueFilter();
        var a = f.Push("See [1] and [note] and [emote:dance] ok");
        var b = f.Flush();
        Assert.Equal("See [1] and [note] and [emote:dance] ok", a.Text + b.Text);
        Assert.Empty(a.Cues);
    }

    [Fact]
    public void UnclosedBracketIsTextAtTheEnd()
    {
        var f = new CueFilter();
        var a = f.Push("array[i");
        var b = f.Flush();
        Assert.Equal("array[i", a.Text + b.Text);
    }

    [Fact]
    public void DropsThinkBlocksEvenWhenSplit()
    {
        var f = new CueFilter();
        var parts = new[] { "<thi", "nk>secret plan</th", "ink>Answer", " here" };
        var text = string.Concat(parts.Select(p => f.Push(p).Text)) + f.Flush().Text;
        Assert.Equal("Answer here", text);
    }

    [Theory]
    [InlineData("thanks so much!", "bow", null)]
    [InlineData("haha that's a good joke", "laugh", "laugh")]
    [InlineData("ugh this is so frustrating", "comfort", "upset")]
    [InlineData("hi there", "wave", "wave")]
    [InlineData("Hey, a question", "wave", "wave")]
    public void KeywordFallback(string text, string npc, string? player)
    {
        Assert.Equal((npc, player), KeywordCues.Guess(text));
    }

    [Theory]
    [InlineData("which way to the aetheryte")]
    [InlineData("this is fine")]
    [InlineData("")]
    public void KeywordFallbackStaysQuiet(string text) => Assert.Null(KeywordCues.Guess(text));

    [Fact]
    public void EmoteWhitelistIsCompleteAndResolvesAliases()
    {
        Assert.All(EmoteTable.All, e =>
        {
            Assert.True(e.EmoteId > 0);
            Assert.True(e.TimelineId > 0);
            Assert.StartsWith("emote/", e.TimelineKey);
        });
        Assert.Equal(EmoteTable.All.Count, EmoteTable.All.Select(e => e.Name).Distinct().Count());
        Assert.Equal("yes", EmoteTable.Find("nod")!.Name);
        Assert.Equal(739, EmoteTable.Find("/NOD")!.TimelineId);
        Assert.Null(EmoteTable.Find("dance"));
        Assert.Null(EmoteTable.Find("sit"));
    }
}

public class TranscriptTests
{
    private static readonly Persona Scholar = new("Studium researcher", "", SpeakerKind.Npc, "Alice Liddell");

    [Fact]
    public void KeepsFollowUpsAndResendsThem()
    {
        var t = new Transcript();
        t.Ask("What is aether?");
        t.Append("  It is");
        t.Append(" life.");
        t.Finish();
        t.Ask("And ceruleum?");
        var m = t.Messages(Scholar);
        Assert.Equal(["system", "user", "assistant", "user"], m.Select(x => x.Role));
        Assert.Equal("It is life.", m[2].Content);
        Assert.True(t.Pending);
    }

    [Fact]
    public void FailedAnswersAreNotResent()
    {
        var t = new Transcript();
        t.Ask("first");
        t.Fail("gateway down");
        Assert.True(t.Turns[^1].Failed);
        t.Ask("second");
        var m = t.Messages(Scholar);
        Assert.Equal(["system", "user"], m.Select(x => x.Role));
        Assert.Equal("second", m[1].Content);
    }

    [Fact]
    public void TrimsOldestTurnsToTheBudgetAndStartsOnAUserTurn()
    {
        var t = new Transcript();
        for (var i = 0; i < 20; i++)
        {
            t.Ask($"question {i} " + new string('q', 200));
            t.Append($"answer {i} " + new string('a', 300));
            t.Finish();
        }

        t.Ask("latest");
        var m = t.Messages(Scholar, maxChars: 2000);
        Assert.Equal("user", m[1].Role);
        Assert.Equal("latest", m[^1].Content);
        Assert.True(m.Skip(1).Sum(x => x.Content.Length) <= 2000);
        Assert.True(m.Count < 42);
    }

    [Fact]
    public void RequestIsStreamingChatCompletions()
    {
        var t = new Transcript();
        t.Ask("hello");
        using var doc = JsonDocument.Parse(t.BuildRequest(Scholar, "local"));
        var root = doc.RootElement;
        Assert.True(root.GetProperty("stream").GetBoolean());
        Assert.Equal("local", root.GetProperty("model").GetString());
        var msgs = root.GetProperty("messages");
        Assert.Equal("system", msgs[0].GetProperty("role").GetString());
        Assert.Contains("[emote:", msgs[0].GetProperty("content").GetString());
        Assert.Equal("hello", msgs[1].GetProperty("content").GetString());
    }

    [Fact]
    public void SelfPersonaIsSecondPersonAdvice()
    {
        var p = Transcript.SystemPrompt(new Persona("Alice", "", SpeakerKind.Self, "Alice Liddell"));
        Assert.Contains("Alice Liddell", p);
        Assert.Contains("second person", p);
        Assert.DoesNotContain("The adventurer asking you", p);
    }

    [Fact]
    public void CannotAskWhileStreaming()
    {
        var t = new Transcript();
        t.Ask("one");
        Assert.Throws<InvalidOperationException>(() => t.Ask("two"));
    }
}

public class RevealTests
{
    [Fact]
    public void RevealsAtTheConfiguredRate()
    {
        var r = new TextReveal(10) { SentencePause = 0, ClausePause = 0 };
        var text = "abcdefghijklmnopqrst";
        for (var i = 0; i < 10; i++)
            r.Advance(0.1, text);
        Assert.InRange(r.Visible, 9, 11);
    }

    [Fact]
    public void PausesAfterSentencesNotInsideNumbers()
    {
        var r = new TextReveal(100);
        Assert.Equal(r.SentencePause, r.PauseAfter("Hi. There", 2));
        Assert.Equal(r.ClausePause, r.PauseAfter("a, b", 1));
        Assert.Equal(0, r.PauseAfter("3.5 yalms", 1));
    }

    [Fact]
    public void NeverRunsAheadOfStreamingTextAndDoesNotBurst()
    {
        var r = new TextReveal(20);
        var text = "Hello";
        for (var i = 0; i < 50; i++)
            r.Advance(0.1, text); // five seconds waiting on a five-character text
        Assert.Equal(5, r.Visible);
        text += " world, and more";
        r.Advance(0.05, text);
        Assert.True(r.Visible <= 8, $"burst to {r.Visible}");
    }

    [Fact]
    public void SkipAndInstant()
    {
        var r = new TextReveal(10);
        r.Skip("abc");
        Assert.True(r.Caught("abc"));
        var instant = new TextReveal(TextReveal.RateFor(5));
        Assert.Equal(11, instant.Advance(0.001, "hello world"));
        Assert.True(TextReveal.RateFor(1) < TextReveal.RateFor(3));
    }
}

public class FollowTests
{
    [Fact]
    public void SpawnsInFrontFacingThePlayer()
    {
        var f = new Follower();
        f.Spawn(10, 0, 20, 0, 2);
        Assert.Equal(10, f.X, 3);
        Assert.Equal(22, f.Z, 3);
        Assert.Equal(Follower.YawTowards(f.X, f.Z, 10, 20), f.Yaw, 3);
        Assert.True(f.Conversing);
    }

    [Fact]
    public void FollowsBesideTheWalkingPlayerAndWalks()
    {
        var f = new Follower();
        f.Spawn(0, 0, 0, 0);
        double pz = 0;
        var gaits = new List<Gait>();
        for (var i = 0; i < 240; i++)
        {
            pz += 2.5 / 60; // walking forward (+z) at 2.5 yalms/s
            f.Update(1 / 60.0, 0, 0, pz, 0, talking: false, camX: 0, camZ: pz - 6);
            gaits.Add(f.Gait);
        }

        Assert.False(f.Conversing);
        var d = Math.Sqrt(f.X * f.X + (f.Z - pz) * (f.Z - pz));
        Assert.InRange(d, 1.1, 3.0);
        Assert.True(f.Z > pz - 1.0, "never trails behind the player");
        Assert.Contains(Gait.Walk, gaits);
    }

    [Fact]
    public void RunsWhenThePlayerRunsAndIdlesWhenStopped()
    {
        var f = new Follower();
        f.Spawn(0, 0, 0, 0);
        double pz = 0;
        for (var i = 0; i < 180; i++)
        {
            pz += 6.5 / 60;
            f.Update(1 / 60.0, 0, 0, pz, 0, false);
        }

        Assert.Equal(Gait.Run, f.Gait);
        for (var i = 0; i < 240; i++)
            f.Update(1 / 60.0, 0, 0, pz, 0, false);
        Assert.Equal(Gait.Idle, f.Gait);
        Assert.Equal(Follower.YawTowards(f.X, f.Z, 0, pz), f.Yaw, 1);
    }

    [Fact]
    public void KeepsOffTheCameraLine()
    {
        var (x, z) = Follower.ClearOfSegment(0, -3, 0, -6, 0, 0, 0.9);
        Assert.Equal(0.9, Math.Abs(x), 3);
        var (x2, z2) = Follower.ClearOfSegment(2, -3, 0, -6, 0, 0, 0.9);
        Assert.Equal((2.0, -3.0), (x2, z2));
        _ = z;
    }

    [Fact]
    public void TurnsTheShortWay()
    {
        var next = Follower.TurnToward(3.0, -3.0, 0.1);
        Assert.True(next > 3.0 || next < -3.0);
        Assert.Equal(1.0, Follower.TurnToward(0.95, 1.0, 0.5), 6);
    }
}

public class SpeakerTests
{
    [Fact]
    public void PresetsResolveToVerifiedRows()
    {
        Assert.Equal("npc:1041316", SpeakerPresets.Resolve("scholar"));
        Assert.Equal("npc:1000063", SpeakerPresets.Resolve("Moogle"));
        Assert.Equal("npc:1040124", SpeakerPresets.Resolve("archivist"));
        Assert.Equal("self", SpeakerPresets.Resolve("myself"));
        Assert.Equal("minion:3", SpeakerPresets.Resolve("minion:3"));
        Assert.Null(SpeakerPresets.Resolve("nobody"));
        Assert.Equal(SpeakerPresets.DefaultKey, SpeakerPresets.Resolve("scholar"));
    }

    [Theory]
    [InlineData("npc:1040124", SpeakerSource.Npc, 1040124u)]
    [InlineData("MOUNT:1", SpeakerSource.Mount, 1u)]
    [InlineData("pet:6", SpeakerSource.Pet, 6u)]
    [InlineData("self", SpeakerSource.Self, 0u)]
    public void KeysRoundTrip(string key, SpeakerSource source, uint id)
    {
        Assert.Equal((source, id), SpeakerEntry.ParseKey(key));
        Assert.Equal(key.ToLowerInvariant(), new SpeakerEntry(source, id, "x").Key);
    }

    [Theory]
    [InlineData("npc")]
    [InlineData("npc:x")]
    [InlineData("chocobo:1")]
    [InlineData("")]
    public void RejectsBadKeys(string key) => Assert.Null(SpeakerEntry.ParseKey(key));

    private static readonly SpeakerCatalog Catalog = new(
    [
        new(SpeakerSource.Self, 0, "Alice Liddell", "yourself"),
        new(SpeakerSource.Npc, 1003053, "Alphinaud"),
        new(SpeakerSource.Npc, 1004145, "Alphinaud", "", "Limsa Lominsa"),
        new(SpeakerSource.Npc, 1040124, "Approachable archivist", "", "Old Sharlayan"),
        new(SpeakerSource.Npc, 1000063, "Delivery moogle", "", "Limsa Lominsa", 15),
        new(SpeakerSource.Minion, 3, "Cherry bomb", "minion", "", 409),
        new(SpeakerSource.Mount, 1, "Company chocobo", "mount", "", 1),
    ]);

    [Fact]
    public void SearchDedupesNamesAndPrefersLocatedRows()
    {
        var r = Catalog.Search("alphinaud", null, [], []);
        Assert.Single(r);
        Assert.Equal("Limsa Lominsa", r[0].Location);
    }

    [Fact]
    public void SearchFiltersBySourceAndRanksFavourites()
    {
        Assert.Equal(["Company chocobo"], Catalog.Search("c", SpeakerSource.Mount, [], []).Select(e => e.Name));
        var r = Catalog.Search("a", SpeakerSource.Npc, ["npc:1040124"], []);
        Assert.Equal("Approachable archivist", r[0].Name);
    }

    [Fact]
    public void EmptyQueryListsFavouritesThenRecents()
    {
        var r = Catalog.Search("", null, ["mount:1"], ["minion:3"]);
        Assert.Equal(["Company chocobo", "Cherry bomb"], r.Take(2).Select(e => e.Name));
    }

    [Fact]
    public void OnlyTalkersGetEmotes()
    {
        Assert.True(Catalog.Find("npc:1040124")!.Talks);
        Assert.True(Catalog.Find("self")!.Talks);
        Assert.False(Catalog.Find("minion:3")!.Talks);
        Assert.Equal(SpeakerKind.Self, Catalog.Find("self")!.Kind);
    }
}

public class NpcLookTests
{
    [Fact]
    public void BuildsCustomizeAndEquipmentFromSheetValues()
    {
        var customize = Enumerable.Range(1, 26).Select(i => (byte)i).ToArray();
        uint[] models = [74748, 140284, 75439, 74748, 74748, 0, 0, 0, 0x0001_0011, 0x0002_0022];
        byte[] stains = [0, 6, 0, 6, 6, 0, 0, 0, 1, 2];
        var look = NpcLook.FromENpc(0, customize, models, stains, new byte[10], 0x0001_0002_0003UL, 0);
        Assert.True(look.IsHuman);
        Assert.Equal(customize, look.Customize);
        Assert.Equal(new EquipModel(9212, 2, 6), look.Equipment[1]); // 140284 = 2 << 16 | 9212
        Assert.Equal(new EquipModel(0x0022, 2, 2), look.Equipment[8]); // sheet RightRing → game slot 8
        Assert.Equal(new EquipModel(0x0011, 1, 1), look.Equipment[9]); // sheet LeftRing → game slot 9
        Assert.Equal(new WeaponModel(3, 2, 1), look.MainHand);
        Assert.True(look.OffHand.IsEmpty);
    }

    [Fact]
    public void ModelOnlyLooks()
    {
        var m = NpcLook.Model(15);
        Assert.False(m.IsHuman);
        Assert.Equal(NpcLook.CustomizeLength, m.Customize.Length);
    }

    [Fact]
    public void TokenPathFollowsTheHostMapping()
    {
        Assert.Equal(@"Z:\home\alice\.config\almanac\token", AlmanacPaths.TokenPath(HostPaths.Wine("/home/alice")));
        Assert.Equal("/home/alice/.config/almanac/token", AlmanacPaths.TokenPath(HostPaths.Native("/home/alice")));
        Assert.Null(AlmanacPaths.TokenPath(HostPaths.Native("")));
        Assert.Equal("http://127.0.0.1:41881/v1/chat/completions", AlmanacPaths.ChatUrl("").ToString());
        Assert.Equal("http://h:1/v1/chat/completions", AlmanacPaths.ChatUrl("http://h:1/v1/").ToString());
    }
}
