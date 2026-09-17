using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Frugal.Providers;

/// <summary>
/// Any provider that speaks the OpenAI chat-completions dialect: OpenAI itself, OpenRouter,
/// Groq, Together, vLLM, and so on.
/// </summary>
public sealed class OpenAICompatibleProvider : IProvider
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(10) };

    public string Name { get; }
    private readonly string _baseUrl;
    private readonly string _apiKeyEnv;
    private readonly IReadOnlyDictionary<string, string> _extraHeaders;
    private readonly bool _useMaxCompletionTokens;

    public OpenAICompatibleProvider(string name, string baseUrl, string apiKeyEnv,
        IReadOnlyDictionary<string, string>? extraHeaders = null, bool useMaxCompletionTokens = false)
    {
        Name = name;
        _baseUrl = baseUrl.TrimEnd('/');
        _apiKeyEnv = apiKeyEnv;
        _extraHeaders = extraHeaders ?? new Dictionary<string, string>();
        _useMaxCompletionTokens = useMaxCompletionTokens;
    }

    public static OpenAICompatibleProvider OpenAI() => new(
        "openai", Env.Get(Env.OpenAIBaseUrl) ?? "https://api.openai.com/v1", Env.OpenAI, useMaxCompletionTokens: true);

    public static OpenAICompatibleProvider OpenRouter() => new(
        "openrouter", "https://openrouter.ai/api/v1", Env.OpenRouter,
        new Dictionary<string, string> { ["HTTP-Referer"] = "https://github.com/frugal-router", ["X-Title"] = "frugal" });

    private JsonObject Body(ChatRequest req, ModelSpec spec, string? effort, bool stream)
    {
        var body = JsonSerializer.SerializeToNode(req, Json.Wire)!.AsObject();
        body.Remove("frugal_options");
        body["model"] = spec.Model;
        body["stream"] = stream;
        if (effort is not null && spec.Effort.Count > 0) body["reasoning_effort"] = effort;
        else body.Remove("reasoning_effort");
        if (_useMaxCompletionTokens && body.ContainsKey("max_tokens") && !body.ContainsKey("max_completion_tokens"))
        {
            body["max_completion_tokens"] = body["max_tokens"]!.DeepClone();
            body.Remove("max_tokens");
        }
        if (stream)
        {
            var so = body["stream_options"] as JsonObject ?? new JsonObject();
            so["include_usage"] = true;
            body["stream_options"] = so;
        }
        else body.Remove("stream_options");
        return body;
    }

    private async Task<HttpResponseMessage> PostAsync(JsonObject body, CancellationToken ct)
    {
        var key = Env.Get(_apiKeyEnv);
        if (string.IsNullOrEmpty(key)) throw new UpstreamException(Name, 0, $"{_apiKeyEnv} is not set");
        var req = new HttpRequestMessage(HttpMethod.Post, $"{_baseUrl}/chat/completions")
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
        };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        foreach (var (k, v) in _extraHeaders) req.Headers.TryAddWithoutValidation(k, v);
        var res = await Http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!res.IsSuccessStatusCode)
        {
            var text = await res.Content.ReadAsStringAsync(ct);
            res.Dispose();
            throw new UpstreamException(Name, (int)res.StatusCode, text.Length > 2000 ? text[..2000] : text);
        }
        return res;
    }

    public async Task<JsonObject> CompleteAsync(ChatRequest req, ModelSpec spec, string? effort, CancellationToken ct)
    {
        using var res = await PostAsync(Body(req, spec, effort, false), ct);
        var node = await JsonNode.ParseAsync(await res.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
        return node as JsonObject ?? throw new UpstreamException(Name, 502, "non-object response");
    }

    public async IAsyncEnumerable<JsonObject> StreamAsync(ChatRequest req, ModelSpec spec, string? effort,
        [EnumeratorCancellation] CancellationToken ct)
    {
        using var res = await PostAsync(Body(req, spec, effort, true), ct);
        using var stream = await res.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        while (await reader.ReadLineAsync(ct) is { } line)
        {
            line = line.Trim();
            if (!line.StartsWith("data:", StringComparison.Ordinal)) continue;
            var data = line[5..].Trim();
            if (data == "[DONE]") yield break;
            JsonObject? chunk = null;
            try { chunk = JsonNode.Parse(data) as JsonObject; }
            catch (JsonException) { /* keep-alive or partial line */ }
            if (chunk is not null) yield return chunk;
        }
    }
}
