using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Anthropic;
using Anthropic.Models.Messages;

namespace LlmDispatcher.Providers;

/// <summary>Bridges the OpenAI chat dialect onto the Anthropic Messages API via the official SDK.</summary>
public sealed partial class AnthropicProvider : IProvider
{
    private const int DefaultMaxTokens = 16_000;
    private const int DefaultMaxTokensStream = 32_000;

    public string Name => "anthropic";
    private AnthropicClient? _client;

    private AnthropicClient Client()
    {
        if (!Env.Has(Env.Anthropic)) throw new UpstreamException(Name, 0, "ANTHROPIC_API_KEY is not set");
        return _client ??= new AnthropicClient();
    }

    public async Task<JsonObject> CompleteAsync(ChatRequest req, ModelSpec spec, string? effort, CancellationToken ct)
    {
        var parameters = ToAnthropicParams(req, spec, effort, stream: false);
        try
        {
            var msg = await Client().Messages.Create(parameters, cancellationToken: ct);
            return FromAnthropicMessage(msg, spec);
        }
        catch (Anthropic.Exceptions.AnthropicApiException ex)
        {
            throw new UpstreamException(Name, (int)ex.StatusCode, ex.Message);
        }
    }

    public async IAsyncEnumerable<JsonObject> StreamAsync(ChatRequest req, ModelSpec spec, string? effort,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var parameters = ToAnthropicParams(req, spec, effort, stream: true);
        var created = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var id = $"chatcmpl-{created}";
        var inputTokens = 0L;
        var outputTokens = 0L;
        string? finish = null;
        var toolIndex = new Dictionary<long, int>();
        var nextTool = 0;

        JsonObject Chunk(JsonObject delta, string? finishReason = null) => new()
        {
            ["id"] = id,
            ["object"] = "chat.completion.chunk",
            ["created"] = created,
            ["model"] = spec.Model,
            ["choices"] = new JsonArray(new JsonObject { ["index"] = 0, ["delta"] = delta, ["finish_reason"] = finishReason }),
        };

        yield return Chunk(new JsonObject { ["role"] = "assistant", ["content"] = "" });

        var events = Client().Messages.CreateStreaming(parameters, cancellationToken: ct);
        await using var e = events.GetAsyncEnumerator(ct);
        while (true)
        {
            bool more;
            try { more = await e.MoveNextAsync(); }
            catch (Anthropic.Exceptions.AnthropicApiException ex) { throw new UpstreamException(Name, (int)ex.StatusCode, ex.Message); }
            if (!more) break;
            var ev = e.Current;

            if (ev.TryPickStart(out var start))
            {
                id = start.Message.ID;
                inputTokens = start.Message.Usage.InputTokens;
            }
            else if (ev.TryPickContentBlockStart(out var cbs))
            {
                if (cbs.ContentBlock.TryPickToolUse(out var tu))
                {
                    var idx = nextTool++;
                    toolIndex[cbs.Index] = idx;
                    yield return Chunk(new JsonObject
                    {
                        ["tool_calls"] = new JsonArray(new JsonObject
                        {
                            ["index"] = idx, ["id"] = tu.ID, ["type"] = "function",
                            ["function"] = new JsonObject { ["name"] = tu.Name, ["arguments"] = "" },
                        }),
                    });
                }
            }
            else if (ev.TryPickContentBlockDelta(out var cbd))
            {
                if (cbd.Delta.TryPickText(out var td))
                    yield return Chunk(new JsonObject { ["content"] = td.Text });
                else if (cbd.Delta.TryPickInputJson(out var ij) && toolIndex.TryGetValue(cbd.Index, out var idx))
                    yield return Chunk(new JsonObject
                    {
                        ["tool_calls"] = new JsonArray(new JsonObject
                        {
                            ["index"] = idx, ["function"] = new JsonObject { ["arguments"] = ij.PartialJson },
                        }),
                    });
            }
            else if (ev.TryPickDelta(out var md))
            {
                finish = MapStop(md.Delta.StopReason?.ToString());
                outputTokens = md.Usage.OutputTokens;
            }
        }

        var last = Chunk(new JsonObject(), finish ?? "stop");
        last["usage"] = new JsonObject
        {
            ["prompt_tokens"] = inputTokens, ["completion_tokens"] = outputTokens, ["total_tokens"] = inputTokens + outputTokens,
        };
        yield return last;
    }

    /* ---------------- request conversion ---------------- */

    public static MessageCreateParams ToAnthropicParams(ChatRequest req, ModelSpec spec, string? effort, bool stream)
    {
        var system = new List<string>();
        var messages = new List<MessageParam>();
        // Consecutive OpenAI "tool" messages collapse into one Anthropic user turn of tool_result blocks.
        var pendingToolResults = new List<ContentBlockParam>();
        void FlushToolResults()
        {
            if (pendingToolResults.Count == 0) return;
            messages.Add(new MessageParam { Role = Role.User, Content = pendingToolResults.ToList() });
            pendingToolResults.Clear();
        }

        foreach (var m in req.Messages)
        {
            if (m.Role != "tool") FlushToolResults();
            switch (m.Role)
            {
                case "system":
                case "developer":
                    system.Add(m.Text());
                    break;
                case "tool":
                    pendingToolResults.Add(new ToolResultBlockParam { ToolUseID = m.ToolCallId ?? "", Content = m.Text() });
                    break;
                case "assistant":
                {
                    var content = new List<ContentBlockParam>();
                    var text = m.Text();
                    if (text.Length > 0) content.Add(new TextBlockParam { Text = text });
                    foreach (var tc in m.ToolCalls ?? []) content.Add(ToolUse(tc));
                    if (content.Count > 0) messages.Add(new MessageParam { Role = Role.Assistant, Content = content });
                    break;
                }
                default:
                    messages.Add(new MessageParam { Role = Role.User, Content = UserContent(m) });
                    break;
            }
        }
        FlushToolResults();
        // Anthropic requires the first message to be from the user.
        if (messages.Count == 0 || messages[0].Role != Role.User)
            messages.Insert(0, new MessageParam { Role = Role.User, Content = "(continue)" });

        var requested = req.RequestedMaxTokens;
        var maxTokens = Math.Min(spec.MaxOutput, requested ?? (stream ? DefaultMaxTokensStream : DefaultMaxTokens));

        var hasTools = req.Tools is { Count: > 0 };
        List<string>? stopSequences = req.Stop is { } stop
            ? stop.ValueKind == JsonValueKind.Array
                ? stop.EnumerateArray().Select(s => s.GetString() ?? "").ToList()
                : [stop.GetString() ?? ""]
            : null;
        // Sampling parameters are rejected by Claude 4.7+ / 5.x; only older models accept them.
        var sampling = SamplingModel().IsMatch(spec.Model);
        // Anthropic accepts low..max; the catalog decides which of those a model supports.
        var useEffort = effort is not null && spec.Effort.Contains(effort) && effort is not ("none" or "minimal");

#pragma warning disable CS0618 // temperature/top_p are deliberately limited to the models that still accept them
        return new MessageCreateParams
        {
            Model = spec.Model,
            MaxTokens = maxTokens,
            Messages = messages,
            System = system.Count > 0 ? string.Join("\n\n", system) : null,
            Tools = hasTools ? req.Tools!.Select(t => (ToolUnion)ToTool(t)).ToList() : null,
            ToolChoice = hasTools ? ToolChoiceFor(req.ToolChoice, spec) : null,
            StopSequences = stopSequences,
            Temperature = sampling ? req.Temperature : null,
            TopP = sampling && req.Temperature is null ? req.TopP : null,
            OutputConfig = useEffort ? new OutputConfig { Effort = EffortOf(effort!) } : null,
        };
#pragma warning restore CS0618
    }

    private static Effort EffortOf(string effort) => effort switch
    {
        "low" => Effort.Low,
        "medium" => Effort.Medium,
        "high" => Effort.High,
        "xhigh" => Effort.Xhigh,
        _ => Effort.Max,
    };

    private static Tool ToTool(ToolDef t)
    {
        Dictionary<string, JsonElement>? properties = null;
        List<string>? required = null;
        if (t.Function.Parameters is { ValueKind: JsonValueKind.Object } ps)
        {
            if (ps.TryGetProperty("properties", out var props) && props.ValueKind == JsonValueKind.Object)
                properties = props.EnumerateObject().ToDictionary(x => x.Name, x => x.Value.Clone());
            if (ps.TryGetProperty("required", out var reqd) && reqd.ValueKind == JsonValueKind.Array)
                required = reqd.EnumerateArray().Select(x => x.GetString() ?? "").ToList();
        }
        return new Tool
        {
            Name = t.Function.Name,
            Description = t.Function.Description,
            InputSchema = new InputSchema { Properties = properties, Required = required },
        };
    }

    private static ToolChoice? ToolChoiceFor(JsonElement? tc, ModelSpec spec)
    {
        if (tc is not { } el) return null;
        if (el.ValueKind == JsonValueKind.String)
        {
            var s = el.GetString();
            if (s == "none") return new ToolChoiceNone();
            if (s == "required") return ForcedModel().IsMatch(spec.Model) ? new ToolChoiceAuto() : new ToolChoiceAny();
            return null; // "auto" is the default
        }
        if (el.ValueKind == JsonValueKind.Object && el.TryGetProperty("function", out var fn) && fn.TryGetProperty("name", out var name))
        {
            // Forced tool use is rejected by Claude Fable / Mythos; fall back to auto there.
            if (ForcedModel().IsMatch(spec.Model)) return new ToolChoiceAuto();
            return new ToolChoiceTool { Name = name.GetString() ?? "" };
        }
        return null;
    }

    private static ToolUseBlockParam ToolUse(ToolCall tc)
    {
        Dictionary<string, JsonElement> input;
        try
        {
            input = string.IsNullOrWhiteSpace(tc.Function.Arguments)
                ? []
                : JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(tc.Function.Arguments) ?? [];
        }
        catch (JsonException)
        {
            input = new Dictionary<string, JsonElement> { ["_raw"] = JsonSerializer.SerializeToElement(tc.Function.Arguments) };
        }
        return new ToolUseBlockParam { ID = tc.Id, Name = tc.Function.Name, Input = input };
    }

    private static MessageParamContent UserContent(ChatMessage m)
    {
        if (m.Content is not { } c) return "";
        if (c.ValueKind == JsonValueKind.String) return c.GetString() ?? "";
        if (c.ValueKind != JsonValueKind.Array) return "";
        var blocks = new List<ContentBlockParam>();
        foreach (var part in c.EnumerateArray())
        {
            var type = part.TryGetProperty("type", out var t) ? t.GetString() : null;
            if (type == "text" && part.TryGetProperty("text", out var txt))
                blocks.Add(new TextBlockParam { Text = txt.GetString() ?? "" });
            else if (type == "image_url" && part.TryGetProperty("image_url", out var iu) && iu.TryGetProperty("url", out var u))
                blocks.Add(new ImageBlockParam { Source = ImageSource(u.GetString() ?? "") });
        }
        return blocks.Count > 0 ? blocks : "";
    }

    private static ImageBlockParamSource ImageSource(string url)
    {
        var m = DataUrl().Match(url);
        if (m.Success)
        {
            var media = m.Groups[1].Value.ToLowerInvariant() switch
            {
                "image/jpeg" => MediaType.ImageJpeg,
                "image/gif" => MediaType.ImageGif,
                "image/webp" => MediaType.ImageWebP,
                _ => MediaType.ImagePng,
            };
            return new Base64ImageSource { MediaType = media, Data = m.Groups[2].Value };
        }
        return new UrlImageSource { Url = url };
    }

    /* ---------------- response conversion ---------------- */

    public static JsonObject FromAnthropicMessage(Message msg, ModelSpec spec)
    {
        var text = new System.Text.StringBuilder();
        var toolCalls = new JsonArray();
        foreach (var block in msg.Content)
        {
            if (block.TryPickText(out var tb)) text.Append(tb.Text);
            else if (block.TryPickToolUse(out var tu))
                toolCalls.Add(new JsonObject
                {
                    ["id"] = tu.ID, ["type"] = "function",
                    ["function"] = new JsonObject { ["name"] = tu.Name, ["arguments"] = JsonSerializer.Serialize(tu.Input) },
                });
        }
        var message = new JsonObject
        {
            ["role"] = "assistant",
            ["content"] = text.Length > 0 ? text.ToString() : toolCalls.Count > 0 ? null : "",
        };
        if (toolCalls.Count > 0) message["tool_calls"] = toolCalls;
        var input = msg.Usage.InputTokens;
        var output = msg.Usage.OutputTokens;
        return new JsonObject
        {
            ["id"] = msg.ID,
            ["object"] = "chat.completion",
            ["created"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            ["model"] = spec.Model,
            ["choices"] = new JsonArray(new JsonObject { ["index"] = 0, ["message"] = message, ["finish_reason"] = MapStop(msg.StopReason?.ToString()) }),
            ["usage"] = new JsonObject { ["prompt_tokens"] = input, ["completion_tokens"] = output, ["total_tokens"] = input + output },
        };
    }

    public static string? MapStop(string? reason)
    {
        var key = reason?.Replace("_", "").ToLowerInvariant();
        return key switch
        {
            "endturn" or "stopsequence" or "pauseturn" => "stop",
            "maxtokens" => "length",
            "tooluse" => "tool_calls",
            "refusal" => "content_filter",
            _ => null,
        };
    }

    [GeneratedRegex(@"^data:(image/(?:png|jpeg|gif|webp));base64,(.+)$", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex DataUrl();

    [GeneratedRegex("fable|mythos")]
    private static partial Regex ForcedModel();

    [GeneratedRegex("haiku-4-5|-4-6")]
    private static partial Regex SamplingModel();
}
