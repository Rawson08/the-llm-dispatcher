using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace LlmDispatcher;

/// <summary>Canonical effort ladder, lowest to highest. Providers support subsets.</summary>
public static class Efforts
{
    public static readonly string[] Ladder = ["none", "minimal", "low", "medium", "high", "xhigh", "max"];
    public static int Rank(string effort) => Array.IndexOf(Ladder, effort);
    public static bool IsValid(string? effort) => effort is not null && Rank(effort) >= 0;
}

public static class Tasks
{
    public static readonly string[] All =
        ["coding", "writing", "conversation", "analysis", "math", "extraction", "summarization", "creative", "agentic", "other"];
}

public sealed class Price
{
    public double Input { get; set; }
    public double Output { get; set; }
}

/// <summary>
/// An OpenAI-compatible endpoint declared in the catalog's "providers" section. "anthropic",
/// "openai" and "openrouter" are built in; anything else (Ollama, LM Studio, vLLM) is declared here.
/// </summary>
public sealed class ProviderConfig
{
    public string BaseUrl { get; set; } = "";
    /// <summary>Env var holding the bearer key; null means the endpoint needs no key.</summary>
    public string? ApiKeyEnv { get; set; }
    /// <summary>False keeps a keyless provider (a local server) out of routing until you opt in.</summary>
    public bool? Enabled { get; set; }
    public Dictionary<string, string>? Headers { get; set; }
    /// <summary>OpenAI's newer models reject <c>max_tokens</c>; true renames it.</summary>
    public bool? UseMaxCompletionTokens { get; set; }
}

/// <summary>One catalog entry. Tier is your quality belief: 1 economy, 2 standard, 3 frontier.</summary>
public sealed class ModelSpec
{
    public string Id { get; set; } = "";
    public string Provider { get; set; } = "";
    public string Model { get; set; } = "";
    public string Name { get; set; } = "";
    public int Tier { get; set; }
    /// <summary>USD per 1M tokens.</summary>
    public Price Price { get; set; } = new();
    public int Context { get; set; }
    public int MaxOutput { get; set; }
    public bool Vision { get; set; }
    public bool Tools { get; set; }
    /// <summary>Effort levels the provider accepts for this model; empty means none.</summary>
    public List<string> Effort { get; set; } = [];
    public List<string>? Strengths { get; set; }
    public List<string>? Weaknesses { get; set; }
}

/* ---------- OpenAI-compatible chat wire format (the proxy's public surface) ---------- */

public sealed class ToolCallFunction
{
    public string Name { get; set; } = "";
    public string Arguments { get; set; } = "";
}

public sealed class ToolCall
{
    public string Id { get; set; } = "";
    public string Type { get; set; } = "function";
    public ToolCallFunction Function { get; set; } = new();
}

public sealed class ChatMessage
{
    public string Role { get; set; } = "user";
    /// <summary>A string, an array of content parts, or null.</summary>
    public JsonElement? Content { get; set; }
    public string? Name { get; set; }
    public List<ToolCall>? ToolCalls { get; set; }
    public string? ToolCallId { get; set; }

    /// <summary>Plain text of the message; images become "[image]".</summary>
    public string Text()
    {
        if (Content is not { } c) return "";
        if (c.ValueKind == JsonValueKind.String) return c.GetString() ?? "";
        if (c.ValueKind != JsonValueKind.Array) return "";
        var parts = new List<string>();
        foreach (var p in c.EnumerateArray())
        {
            var type = p.TryGetProperty("type", out var t) ? t.GetString() : null;
            if (type == "text" && p.TryGetProperty("text", out var txt)) parts.Add(txt.GetString() ?? "");
            else if (IsImagePart(type)) parts.Add("[image]");
        }
        return string.Join("\n", parts);
    }

    public bool HasImage()
    {
        if (Content is not { ValueKind: JsonValueKind.Array } c) return false;
        foreach (var p in c.EnumerateArray())
            if (p.TryGetProperty("type", out var t) && IsImagePart(t.GetString())) return true;
        return false;
    }

    public static bool IsImagePart(string? type) => type is "image_url" or "input_image" or "image";
}

public sealed class ToolFunctionDef
{
    public string Name { get; set; } = "";
    public string? Description { get; set; }
    public JsonElement? Parameters { get; set; }
    public bool? Strict { get; set; }
}

public sealed class ToolDef
{
    public string Type { get; set; } = "function";
    public ToolFunctionDef Function { get; set; } = new();
}

/// <summary>Per-request knobs a client may send under <c>dispatcher_options</c>.</summary>
public sealed class RequestOptions
{
    public string? Baseline { get; set; }
    public int? MinTier { get; set; }
    public int? MaxTier { get; set; }
}

public sealed class ChatRequest
{
    public string Model { get; set; } = "auto";
    public List<ChatMessage> Messages { get; set; } = [];
    public List<ToolDef>? Tools { get; set; }
    public JsonElement? ToolChoice { get; set; }
    public int? MaxTokens { get; set; }
    public int? MaxCompletionTokens { get; set; }
    public double? Temperature { get; set; }
    public double? TopP { get; set; }
    public bool? Stream { get; set; }
    public JsonElement? StreamOptions { get; set; }
    public string? ReasoningEffort { get; set; }
    public JsonElement? Stop { get; set; }
    public RequestOptions? DispatcherOptions { get; set; }

    /// <summary>Anything else the client sent is preserved and forwarded untouched.</summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extra { get; set; }

    public int? RequestedMaxTokens => MaxCompletionTokens ?? MaxTokens;
}

/* ---------- Routing ---------- */

public sealed record Turn(string Role, string Text);

public sealed class Features
{
    public int InputTokens { get; init; }
    public int Turns { get; init; }
    public bool HasImages { get; init; }
    public bool HasTools { get; init; }
    public List<string> ToolNames { get; init; } = [];
    public int? RequestedMaxTokens { get; init; }
    public string SystemExcerpt { get; init; } = "";
    public List<Turn> Recent { get; init; } = [];
    public string LastUser { get; init; } = "";
}

public sealed record Scored(double Score, double Confidence);

public sealed class Judgment
{
    public string Task { get; init; } = "other";
    public double TaskConfidence { get; init; }
    public Dictionary<string, double> TaskProbabilities { get; init; } = [];
    /// <summary>0..4</summary>
    public Scored Difficulty { get; init; } = new(3, 0);
    /// <summary>0..3</summary>
    public Scored Reasoning { get; init; } = new(2, 0);
    /// <summary>Probability that a wrong answer is costly, 0..1.</summary>
    public double Stakes { get; init; } = 0.5;
    /// <summary>0..2</summary>
    public Scored OutputSize { get; init; } = new(1, 0);
    public string Source { get; init; } = "fallback";
    public string? JevModel { get; init; }
    public int JevInputTokens { get; init; }
    public long LatencyMs { get; init; }
    public string? Error { get; init; }

    /// <summary>Conservative judgment used when Jev is unavailable: routes to the safe tier.</summary>
    public static Judgment Fallback(string error, long latencyMs = 0) => new()
    {
        Task = "other", TaskConfidence = 0, Difficulty = new(3, 0), Reasoning = new(2, 0), Stakes = 0.5,
        OutputSize = new(1, 0), Source = "fallback", JevInputTokens = 0, LatencyMs = latencyMs, Error = error,
    };
}

public sealed record Candidate(string Id, double EstimatedCost, int EffectiveTier);

public sealed class Estimate
{
    public int InputTokens { get; set; }
    public int OutputTokens { get; set; }
    public double Cost { get; set; }
    public string BaselineModel { get; set; } = "";
    public double BaselineCost { get; set; }
    public double Savings { get; set; }
}

public sealed class Decision
{
    public ModelSpec Model { get; set; } = new();
    public string? Effort { get; set; }
    public int Tier { get; set; }
    public string Task { get; set; } = "other";
    public Judgment Judgment { get; set; } = Judgment.Fallback("uninitialised");
    public Estimate Estimate { get; set; } = new();
    /// <summary>Kept so the ledger can price actual usage at baseline rates.</summary>
    public ModelSpec? Baseline { get; set; }
    public List<string> Rationale { get; set; } = [];
    public List<Candidate> Candidates { get; set; } = [];
    /// <summary>True when the client named a concrete model and no routing happened.</summary>
    public bool Passthrough { get; set; }

    public string Source => Passthrough ? "passthrough" : Judgment.Source;

    /// <summary>Compact form attached to responses and headers. Never contains prompt text.</summary>
    public JsonObject Summary(bool includeRationale = true)
    {
        var o = new JsonObject
        {
            ["model"] = Model.Id,
            ["provider"] = Model.Provider,
            ["effort"] = Effort,
            ["tier"] = Tier,
            ["task"] = Task,
            ["difficulty"] = Math.Round(Judgment.Difficulty.Score, 2),
            ["reasoning"] = Math.Round(Judgment.Reasoning.Score, 2),
            ["stakes"] = Math.Round(Judgment.Stakes, 2),
            ["confidence"] = new JsonObject
            {
                ["task"] = Math.Round(Judgment.TaskConfidence, 2),
                ["difficulty"] = Math.Round(Judgment.Difficulty.Confidence, 2),
            },
            ["source"] = Source,
            ["estimated_cost_usd"] = Math.Round(Estimate.Cost, 6),
            ["baseline_model"] = Estimate.BaselineModel,
            ["baseline_cost_usd"] = Math.Round(Estimate.BaselineCost, 6),
            ["estimated_savings_usd"] = Math.Round(Estimate.Savings, 6),
        };
        if (includeRationale) o["rationale"] = new JsonArray(Rationale.Select(r => (JsonNode)r!).ToArray());
        return o;
    }
}

public static class Json
{
    /// <summary>snake_case wire format, nulls omitted. Used for everything that crosses HTTP.</summary>
    public static readonly JsonSerializerOptions Wire = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNameCaseInsensitive = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
    };

    /// <summary>camelCase, used for the catalog file.</summary>
    public static readonly JsonSerializerOptions Camel = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };
}
