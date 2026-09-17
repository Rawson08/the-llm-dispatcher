using System.Text.Json.Nodes;

namespace Frugal.Providers;

/// <summary>
/// A downstream LLM provider. Both methods take the client's OpenAI-style request and return
/// OpenAI-style JSON (a full completion, or chat.completion.chunk objects), so the proxy never
/// needs to know which dialect ran underneath.
/// </summary>
public interface IProvider
{
    string Name { get; }
    Task<JsonObject> CompleteAsync(ChatRequest req, ModelSpec spec, string? effort, CancellationToken ct);
    IAsyncEnumerable<JsonObject> StreamAsync(ChatRequest req, ModelSpec spec, string? effort, CancellationToken ct);
}

public sealed class UpstreamException(string provider, int status, string message)
    : Exception($"{provider} upstream {status}: {message}")
{
    public string Provider { get; } = provider;
    public int Status { get; } = status;
}
