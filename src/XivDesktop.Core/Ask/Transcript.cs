using System.Text.Json;
using System.Text.Json.Nodes;

namespace XivDesktop.Core.Ask;

public enum TurnRole
{
    User,
    Assistant,
}

/// <summary>One line of the conversation. An assistant turn grows while its answer streams in.</summary>
public sealed class Turn
{
    public Turn(TurnRole role, string text, bool complete = true)
    {
        Role = role;
        Text = text;
        Complete = complete;
    }

    public TurnRole Role { get; }

    public string Text { get; internal set; }

    /// <summary>False while an answer is still streaming.</summary>
    public bool Complete { get; internal set; }

    /// <summary>The answer failed (the text says why); it is not sent back to the model.</summary>
    public bool Failed { get; internal set; }
}

/// <summary>Who the player is talking to, for the system prompt.</summary>
public sealed record Persona(string Name, string Title = "", SpeakerKind Kind = SpeakerKind.Npc, string PlayerName = "");

/// <summary>What the speaker is, which changes how the model is asked to play it.</summary>
public enum SpeakerKind
{
    Npc,
    Minion,
    Mount,
    Pet,

    /// <summary>A copy of the player: "Alice mode", the player's own inner advisor in the second person.</summary>
    Self,
}

/// <summary>
/// The conversation, kept client-side: almanac's gateway is a stateless proxy (OpenAI/Anthropic wire
/// protocols in front of Ollama) with no session id, so every follow-up resends the whole transcript,
/// trimmed from the oldest turns to a character budget. Not thread-safe: the owner mutates it on one
/// thread and hands snapshots (<see cref="BuildRequest"/>) to the network task.
/// </summary>
public sealed class Transcript
{
    private readonly List<Turn> turns = [];

    public IReadOnlyList<Turn> Turns => turns;

    /// <summary>An answer is streaming.</summary>
    public bool Pending => turns.Count > 0 && turns[^1] is { Role: TurnRole.Assistant, Complete: false };

    public void Clear() => turns.Clear();

    /// <summary>Adds the player's line and an empty streaming answer; returns the answer turn.</summary>
    public Turn Ask(string question)
    {
        if (Pending)
            throw new InvalidOperationException("an answer is still streaming");
        turns.Add(new Turn(TurnRole.User, question.Trim()));
        var answer = new Turn(TurnRole.Assistant, "", complete: false);
        turns.Add(answer);
        return answer;
    }

    /// <summary>Appends streamed text to the pending answer. Leading whitespace of an answer is dropped.</summary>
    public void Append(string text)
    {
        if (!Pending || string.IsNullOrEmpty(text))
            return;
        var t = turns[^1];
        t.Text = t.Text.Length == 0 ? t.Text + text.TrimStart() : t.Text + text;
    }

    public void Finish()
    {
        if (!Pending)
            return;
        var t = turns[^1];
        t.Text = t.Text.TrimEnd();
        t.Complete = true;
        if (t.Text.Length == 0)
        {
            t.Text = "…";
        }
    }

    public void Fail(string reason)
    {
        if (!Pending)
            return;
        var t = turns[^1];
        t.Text = t.Text.Length == 0 ? reason : t.Text.TrimEnd() + " … " + reason;
        t.Complete = true;
        t.Failed = true;
    }

    /// <summary>The system prompt for a persona: stay in character, be brief, and add cue tags.</summary>
    public static string SystemPrompt(Persona p)
    {
        var who = p.Kind switch
        {
            SpeakerKind.Self =>
                $"You are the inner voice of {Or(p.PlayerName, "the player")}, their own wiser self giving advice out loud, the way Alice in Wonderland gave herself very good advice. " +
                "Speak to them in the second person (\"you\"), warmly and candidly, as the part of them that already knows the answer.",
            SpeakerKind.Minion =>
                $"You are {p.Name}, a minion (a small companion creature) in Final Fantasy XIV. You answer in a few short, charming sentences, true to what you are.",
            SpeakerKind.Mount =>
                $"You are {p.Name}, a mount in Final Fantasy XIV. You answer briefly and in character, as a loyal steed who somehow learned to talk.",
            SpeakerKind.Pet =>
                $"You are {p.Name}, a summoned companion of the player in Final Fantasy XIV. You answer briefly and in character.",
            _ =>
                $"You are {p.Name}{(string.IsNullOrWhiteSpace(p.Title) ? "" : $", {p.Title}")}, a character in Final Fantasy XIV. Stay in character and speak as they would.",
        };
        var addressee = p.Kind == SpeakerKind.Self || string.IsNullOrWhiteSpace(p.PlayerName) ? "" : $" The adventurer asking you is {p.PlayerName}.";
        return who + addressee +
               " Answer helpfully and accurately, in plain prose suited to an in-game dialogue box: no Markdown, no lists, no code blocks, at most about 80 words unless asked for more." +
               $" Begin your reply with one tag for your own gesture, like [emote:laugh], and optionally one for the player's reaction, like [player:yes]. Choose only from: {EmoteTable.PromptList}. Do not use tags anywhere else.";
    }

    private static string Or(string s, string fallback) => string.IsNullOrWhiteSpace(s) ? fallback : s;

    /// <summary>
    /// The messages to send: the system prompt, then as many complete, successful turns (plus the new
    /// question) as fit <paramref name="maxChars"/>, oldest dropped first, always starting on a user turn.
    /// </summary>
    public List<(string Role, string Content)> Messages(Persona persona, int maxChars = 12000)
    {
        var history = new List<(string Role, string Content)>();
        for (var i = 0; i < turns.Count; i++)
        {
            var t = turns[i];
            if (t.Role == TurnRole.Assistant && (!t.Complete || t.Failed))
            {
                // Drop the failed answer; its question stays only if it is the one being asked now.
                if (t.Failed && history.Count > 0 && history[^1].Role == "user")
                    history.RemoveAt(history.Count - 1);
                continue;
            }

            history.Add((t.Role == TurnRole.User ? "user" : "assistant", t.Text));
        }

        var budget = Math.Max(1000, maxChars);
        var start = history.Count;
        var used = 0;
        while (start > 0 && used + history[start - 1].Content.Length <= budget)
        {
            start--;
            used += history[start].Content.Length;
        }

        if (start == history.Count && history.Count > 0)
            start = history.Count - 1; // the newest question always goes, truncated below if it must
        while (start < history.Count && history[start].Role != "user")
            start++;

        var messages = new List<(string, string)> { ("system", SystemPrompt(persona)) };
        for (var i = start; i < history.Count; i++)
        {
            var c = history[i].Content;
            messages.Add((history[i].Role, c.Length > budget ? c[..budget] : c));
        }

        return messages;
    }

    /// <summary>An OpenAI chat-completions body (stream=true) for the gateway.</summary>
    public string BuildRequest(Persona persona, string model, int maxChars = 12000, int maxTokens = 600)
    {
        var arr = new JsonArray();
        foreach (var (role, content) in Messages(persona, maxChars))
            arr.Add(new JsonObject { ["role"] = role, ["content"] = content });
        var body = new JsonObject
        {
            ["model"] = model,
            ["stream"] = true,
            ["max_tokens"] = maxTokens,
            // Ollama maps this onto its "think" switch: reasoning would only delay the first word.
            ["reasoning_effort"] = "none",
            ["messages"] = arr,
        };
        return body.ToJsonString(new JsonSerializerOptions { WriteIndented = false });
    }
}
