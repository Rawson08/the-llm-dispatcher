using System.Text.Json;
using Anthropic.Models.Messages;
using LlmDispatcher;
using LlmDispatcher.Providers;

namespace Dispatcher.Tests;

public class FeaturesAndConversionTests
{
    private static readonly ModelSpec Sonnet = new()
    {
        Id = "anthropic/claude-sonnet-5", Provider = "anthropic", Model = "claude-sonnet-5", Name = "Sonnet 5", Tier = 2,
        Price = new() { Input = 2, Output = 10 }, Context = 1_000_000, MaxOutput = 128_000, Vision = true, Tools = true,
        Effort = ["low", "medium", "high", "xhigh", "max"],
    };

    private static ChatRequest Parse(string json) => JsonSerializer.Deserialize<ChatRequest>(json, Json.Wire)!;

    [Fact]
    public void FeaturesTokensImagesToolsLastUser()
    {
        var req = Parse("""
        {
          "model": "auto",
          "messages": [
            {"role": "system", "content": "You are terse."},
            {"role": "user", "content": "first"},
            {"role": "assistant", "content": "ok"},
            {"role": "user", "content": [
              {"type": "text", "text": "what is in this picture?"},
              {"type": "image_url", "image_url": {"url": "data:image/png;base64,AAAA"}}
            ]}
          ],
          "tools": [{"type": "function", "function": {"name": "search", "parameters": {"type": "object"}}}],
          "max_tokens": 300,
          "some_vendor_field": {"x": 1}
        }
        """);
        var f = FeatureExtractor.Extract(req);
        Assert.True(f.HasImages);
        Assert.True(f.HasTools);
        Assert.Equal(["search"], f.ToolNames);
        Assert.Equal(3, f.Turns);
        Assert.Equal("what is in this picture?\n[image]", f.LastUser);
        Assert.Equal("You are terse.", f.SystemExcerpt);
        Assert.Equal(300, f.RequestedMaxTokens);
        Assert.True(f.InputTokens > 20);
        Assert.True(req.Extra!.ContainsKey("some_vendor_field"), "unknown fields survive for passthrough");
    }

    [Fact]
    public void LongMessagesAreTruncatedHeadAndTail()
    {
        var f = FeatureExtractor.Extract(new ChatRequest
        {
            Messages = [new ChatMessage { Role = "user", Content = JsonSerializer.SerializeToElement(new string('x', 20_000)) }],
        });
        Assert.True(f.LastUser.Length < 8_200);
        Assert.Contains("chars omitted", f.LastUser);
    }

    [Fact]
    public void OpenAiChatToAnthropicParams()
    {
        var req = Parse("""
        {
          "model": "auto",
          "messages": [
            {"role": "system", "content": "be brief"},
            {"role": "user", "content": [{"type": "text", "text": "look"}, {"type": "image_url", "image_url": {"url": "data:image/png;base64,QUJD"}}]},
            {"role": "assistant", "content": null, "tool_calls": [{"id": "call_1", "type": "function", "function": {"name": "lookup", "arguments": "{\"q\":\"x\"}"}}]},
            {"role": "tool", "tool_call_id": "call_1", "content": "42"}
          ],
          "tools": [{"type": "function", "function": {"name": "lookup", "description": "d", "parameters": {"type": "object", "properties": {"q": {"type": "string"}}, "required": ["q"]}}}],
          "tool_choice": "required",
          "max_tokens": 500,
          "temperature": 0.2
        }
        """);
        var p = AnthropicProvider.ToAnthropicParams(req, Sonnet, "medium", stream: false);

        Assert.Equal(500, p.MaxTokens);
        Assert.Equal(3, p.Messages.Count);
        Assert.True(p.Messages[0].Role == Role.User);
        Assert.True(p.Messages[1].Role == Role.Assistant);
        Assert.True(p.Messages[2].Role == Role.User);
        Assert.NotNull(p.Tools);
        Assert.Single(p.Tools!);
        Assert.NotNull(p.ToolChoice);
        Assert.True(p.ToolChoice!.TryPickAny(out _));
#pragma warning disable CS0618
        Assert.Null(p.Temperature); // sampling params are dropped for 5.x models
#pragma warning restore CS0618
        Assert.NotNull(p.OutputConfig);
    }

    [Fact]
    public void ForcedToolChoiceDowngradedToAutoOnFable()
    {
        var req = Parse("""
        {"model": "auto", "messages": [{"role": "user", "content": "x"}],
         "tools": [{"type": "function", "function": {"name": "t"}}], "tool_choice": "required"}
        """);
        var fable = new ModelSpec { Id = "anthropic/claude-fable-5-1", Provider = "anthropic", Model = "claude-fable-5-1", Tier = 3, MaxOutput = 128_000, Context = 1_000_000, Vision = true, Tools = true, Effort = ["low", "medium", "high", "xhigh", "max"] };
        var p = AnthropicProvider.ToAnthropicParams(req, fable, null, stream: true);
        Assert.True(p.ToolChoice!.TryPickAuto(out _));
        Assert.Equal(32_000, p.MaxTokens);
        Assert.Null(p.OutputConfig);
    }

    [Fact]
    public void StopReasonMapping()
    {
        Assert.Equal("stop", AnthropicProvider.MapStop("end_turn"));
        Assert.Equal("stop", AnthropicProvider.MapStop("EndTurn"));
        Assert.Equal("length", AnthropicProvider.MapStop("max_tokens"));
        Assert.Equal("tool_calls", AnthropicProvider.MapStop("tool_use"));
        Assert.Equal("content_filter", AnthropicProvider.MapStop("refusal"));
        Assert.Null(AnthropicProvider.MapStop(null));
    }

    [Fact]
    public void EnvParserIgnoresCommentsQuotesAndExistingValues()
    {
        var key = $"DISPATCHER_TEST_{Guid.NewGuid():N}";
        var existing = $"DISPATCHER_TEST_EXISTING_{Guid.NewGuid():N}";
        Environment.SetEnvironmentVariable(existing, "keep");
        Env.Apply([$"# comment", $"{key}=\"quoted value\" ", $"{existing}=override", "export IGNORED_FORMAT"]);
        Assert.Equal("quoted value", Environment.GetEnvironmentVariable(key));
        Assert.Equal("keep", Environment.GetEnvironmentVariable(existing));
    }
}
