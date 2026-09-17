using System.Text.Json.Nodes;
using LlmDispatcher;
using LlmDispatcher.Wrappers;

namespace Dispatcher.Tests;

public class WrapperTests
{
    private static readonly ModelSpec Haiku = new() { Id = "anthropic/claude-haiku-4-5", Provider = "anthropic", Model = "claude-haiku-4-5", Name = "Haiku", Tier = 1, Price = new() { Input = 1, Output = 5 }, Context = 200_000, MaxOutput = 64_000, Vision = true, Tools = true, Effort = [] };
    private static readonly ModelSpec Opus = new() { Id = "anthropic/claude-opus-5", Provider = "anthropic", Model = "claude-opus-5", Name = "Opus", Tier = 3, Price = new() { Input = 5, Output = 25 }, Context = 1_000_000, MaxOutput = 128_000, Vision = true, Tools = true, Effort = ["low", "medium", "high", "xhigh", "max"] };
    private static readonly ModelSpec Sonnet = new() { Id = "anthropic/claude-sonnet-5", Provider = "anthropic", Model = "claude-sonnet-5", Name = "Sonnet", Tier = 2, Price = new() { Input = 2, Output = 10 }, Context = 1_000_000, MaxOutput = 128_000, Vision = true, Tools = true, Effort = ["low", "medium", "high", "xhigh", "max"] };

    private static JsonObject ClaudeBody(JsonObject last, bool tools = true) => new()
    {
        ["model"] = "dispatcher-auto",
        ["max_tokens"] = 32000,
        ["system"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = "x-anthropic-billing-header: claude-code/2.1" }, new JsonObject { ["type"] = "text", ["text"] = "You are Claude Code." }),
        ["tools"] = tools ? new JsonArray(new JsonObject { ["name"] = "Bash" }, new JsonObject { ["name"] = "Read" }) : new JsonArray(),
        ["thinking"] = new JsonObject { ["type"] = "adaptive" },
        ["output_config"] = new JsonObject { ["effort"] = "max" },
        ["context_management"] = new JsonObject { ["edits"] = new JsonArray(new JsonObject { ["type"] = "clear_thinking_20251015" }, new JsonObject { ["type"] = "clear_tool_uses_20250919" }) },
        ["messages"] = new JsonArray(
            new JsonObject { ["role"] = "user", ["content"] = "first" },
            new JsonObject { ["role"] = "assistant", ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = "ok" }) },
            last),
    };

    private static JsonObject User(string text) => new() { ["role"] = "user", ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = text }) };

    private static void AppendOperatorMessages(JsonObject body)
    {
        var msgs = (JsonArray)body["messages"]!;
        msgs.Add(new JsonObject { ["role"] = "system", ["content"] = new JsonArray(), ["output_config"] = new JsonObject { ["effort"] = "max" } });
        msgs.Add(new JsonObject { ["role"] = "system", ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = "Be terse." }) });
    }

    [Fact]
    public void FreshTurnDetectedAndRemindersStripped()
    {
        Assert.Equal("fix the bug", Turns.AnthropicNewTurn(ClaudeBody(User("<system-reminder>noise</system-reminder>fix the bug"))));
    }

    [Fact]
    public void ContinuationsAndAuxiliaryCallsAreNotTurns()
    {
        var toolResult = new JsonObject { ["role"] = "user", ["content"] = new JsonArray(new JsonObject { ["type"] = "tool_result", ["tool_use_id"] = "t1", ["content"] = "out" }) };
        Assert.Null(Turns.AnthropicNewTurn(ClaudeBody(toolResult)));
        Assert.Null(Turns.AnthropicNewTurn(ClaudeBody(new JsonObject { ["role"] = "user", ["content"] = "summarize" }, tools: false)));
        Assert.Null(Turns.AnthropicNewTurn(ClaudeBody(new JsonObject { ["role"] = "assistant", ["content"] = "hi" })));
    }

    [Fact]
    public void TrailingOperatorSystemMessageDoesNotHideTheTurn()
    {
        var body = ClaudeBody(User("refactor auth"));
        AppendOperatorMessages(body);
        Assert.Equal("refactor auth", Turns.AnthropicNewTurn(body));
        var f = Turns.AnthropicFeatures(body);
        Assert.Equal("refactor auth", f.LastUser);
        Assert.Equal("You are Claude Code.", f.SystemExcerpt);
        Assert.Equal(["Bash", "Read"], f.ToolNames);
        Assert.Equal(32000, f.RequestedMaxTokens);
    }

    [Fact]
    public void RoutingToHaikuStripsThinkingEffortAndThinkingEdits()
    {
        var body = Turns.ApplyClaudeModel(ClaudeBody(User("hi")), Haiku, "low");
        Assert.Equal("claude-haiku-4-5", body["model"]!.GetValue<string>());
        Assert.Null(body["thinking"]);
        Assert.Null(body["output_config"]);
        var edits = (JsonArray)((JsonObject)body["context_management"]!)["edits"]!;
        Assert.Single(edits);
        Assert.Equal("clear_tool_uses_20250919", edits[0]!["type"]!.GetValue<string>());
    }

    [Fact]
    public void RoutingToSonnetFoldsOperatorMessagesIntoSystemPrompt()
    {
        var body = ClaudeBody(User("hi"));
        AppendOperatorMessages(body);
        Turns.ApplyClaudeModel(body, Sonnet, "low");
        Assert.DoesNotContain(((JsonArray)body["messages"]!).OfType<JsonObject>(), m => m["role"]!.GetValue<string>() == "system");
        var system = (JsonArray)body["system"]!;
        Assert.Equal("Be terse.", system[^1]!["text"]!.GetValue<string>());
        Assert.Equal("low", body["output_config"]!["effort"]!.GetValue<string>());
        Assert.NotNull(body["thinking"]);
    }

    [Fact]
    public void RoutingToOpusKeepsOperatorMessagesAndAlignsEffort()
    {
        var body = ClaudeBody(User("hi"));
        AppendOperatorMessages(body);
        Turns.ApplyClaudeModel(body, Opus, "medium");
        var sys = ((JsonArray)body["messages"]!).OfType<JsonObject>().Where(m => m["role"]!.GetValue<string>() == "system").ToList();
        Assert.Equal(2, sys.Count);
        Assert.Equal("medium", sys[0]["output_config"]!["effort"]!.GetValue<string>());
        Assert.Equal("medium", body["output_config"]!["effort"]!.GetValue<string>());
    }

    [Fact]
    public void AutoModelDetectionToleratesOneMillionSuffix()
    {
        Assert.True(Turns.IsAutoModel(JsonValue.Create("dispatcher-auto"), "dispatcher-auto"));
        Assert.True(Turns.IsAutoModel(JsonValue.Create("dispatcher-auto[1m]"), "dispatcher-auto"));
        Assert.False(Turns.IsAutoModel(JsonValue.Create("claude-opus-5"), "dispatcher-auto"));
        Assert.False(Turns.IsAutoModel(null, "dispatcher-auto"));
    }

    private static JsonObject CodexBody(params JsonNode[] input) => new()
    {
        ["model"] = "dispatcher-auto",
        ["instructions"] = "You are Codex.",
        ["reasoning"] = new JsonObject { ["effort"] = "xhigh", ["summary"] = "auto" },
        ["tools"] = new JsonArray(new JsonObject { ["type"] = "function", ["name"] = "shell" }),
        ["prompt_cache_key"] = "conv-123",
        ["input"] = new JsonArray(input),
    };

    [Fact]
    public void CodexFreshTurnVersusToolOutputContinuation()
    {
        var fresh = CodexBody(new JsonObject { ["role"] = "user", ["content"] = new JsonArray(new JsonObject { ["type"] = "input_text", ["text"] = "<current_datetime>now</current_datetime>add tests" }) });
        Assert.Equal("add tests", Turns.CodexNewTurn(fresh));
        var cont = CodexBody(new JsonObject { ["role"] = "user", ["content"] = "add tests" }, new JsonObject { ["type"] = "function_call_output", ["call_id"] = "c", ["output"] = "ok" });
        Assert.Null(Turns.CodexNewTurn(cont));
    }

    [Fact]
    public void CodexFeaturesAndConversationKey()
    {
        var body = CodexBody(new JsonObject { ["role"] = "user", ["content"] = "add tests" });
        var f = Turns.CodexFeatures(body);
        Assert.Equal("You are Codex.", f.SystemExcerpt);
        Assert.Equal(["shell"], f.ToolNames);
        Assert.Equal("add tests", f.LastUser);
        var other = CodexBody();
        Assert.Equal(Turns.CodexConversationKey(body), Turns.CodexConversationKey(other));
    }

    [Fact]
    public void CodexModelAndEffortRewritten()
    {
        var luna = new ModelSpec { Id = "openai/gpt-5.6-luna", Provider = "openai", Model = "gpt-5.6-luna", Tier = 1, Effort = ["minimal", "low", "medium", "high", "xhigh"] };
        var body = Turns.ApplyCodexModel(CodexBody(new JsonObject { ["role"] = "user", ["content"] = "hi" }), luna, "low");
        Assert.Equal("gpt-5.6-luna", body["model"]!.GetValue<string>());
        Assert.Equal("low", body["reasoning"]!["effort"]!.GetValue<string>());
        Assert.Equal("auto", body["reasoning"]!["summary"]!.GetValue<string>());
        var floored = Turns.ApplyCodexModel(CodexBody(new JsonObject { ["role"] = "user", ["content"] = "hi" }), luna, "minimal");
        Assert.Equal("low", floored["reasoning"]!["effort"]!.GetValue<string>());
    }

    [Fact]
    public void CleanPromptRemovesBothReminderStyles()
    {
        Assert.Equal("keep", Turns.CleanPrompt("<system_reminder>a</system_reminder> keep <system-reminder>b</system-reminder>"));
    }

    [Fact]
    public void PickerSentinelIsNeverLeftAsSavedDefault()
    {
        var dir = Directory.CreateTempSubdirectory("dispatcher-test-").FullName;
        var file = Path.Combine(dir, "settings.json");
        File.WriteAllText(file, """{"model":"claude-opus-5","other":1}""");
        Assert.Equal("claude-opus-5", ClaudeWrapper.ReadSavedModel(file));
        File.WriteAllText(file, """{"model":"dispatcher-auto","other":1}""");
        Assert.Null(ClaudeWrapper.ReadSavedModel(file));
        Assert.True(ClaudeWrapper.RestoreSavedModel("claude-opus-5", file));
        Assert.Equal("claude-opus-5", JsonNode.Parse(File.ReadAllText(file))!["model"]!.GetValue<string>());
        File.WriteAllText(file, """{"model":"dispatcher-auto"}""");
        Assert.True(ClaudeWrapper.RestoreSavedModel(null, file));
        Assert.Null(JsonNode.Parse(File.ReadAllText(file))!["model"]);
        File.WriteAllText(file, """{"model":"claude-sonnet-5"}""");
        Assert.False(ClaudeWrapper.RestoreSavedModel("claude-opus-5", file));
    }

    [Fact]
    public void ShellQuotingProtectsPromptsWithSpaces()
    {
        Assert.Equal("plain", Launch.QuoteForShell("plain"));
        Assert.Equal("\"fix the bug\"", Launch.QuoteForShell("fix the bug"));
        Assert.Equal("\"say \\\"hi\\\"\"", Launch.QuoteForShell("say \"hi\""));
        Assert.Equal("\"100%%\"", Launch.QuoteForShell("100%"));
    }
}
