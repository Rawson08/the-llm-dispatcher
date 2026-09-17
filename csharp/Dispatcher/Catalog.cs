using System.Text.Json;

namespace LlmDispatcher;

public sealed class CatalogFile
{
    public Dictionary<string, ProviderConfig> Providers { get; init; } = [];
    public List<ModelSpec> Models { get; init; } = [];
}

public static class Catalog
{
    /// <summary>Providers the dispatcher knows without any catalog declaration.</summary>
    public static Dictionary<string, ProviderConfig> BuiltinProviders() => new()
    {
        ["anthropic"] = new ProviderConfig { BaseUrl = "https://api.anthropic.com", ApiKeyEnv = Env.Anthropic },
        ["openai"] = new ProviderConfig { BaseUrl = Env.Get(Env.OpenAIBaseUrl) ?? "https://api.openai.com/v1", ApiKeyEnv = Env.OpenAI, UseMaxCompletionTokens = true },
        ["openrouter"] = new ProviderConfig { BaseUrl = "https://openrouter.ai/api/v1", ApiKeyEnv = Env.OpenRouter },
    };

    /// <summary>The shared catalog lives at the repo root; search there, then next to the binary.</summary>
    public static string DefaultPath()
    {
        var candidates = new List<string>();
        var dir = Directory.GetCurrentDirectory();
        for (var i = 0; i < 4 && dir is not null; i++)
        {
            candidates.Add(Path.Combine(dir, "models.json"));
            dir = Path.GetDirectoryName(dir);
        }
        candidates.Add(Path.Combine(AppContext.BaseDirectory, "models.json"));
        return candidates.FirstOrDefault(File.Exists) ?? candidates[0];
    }

    /// <summary>Load models and custom providers from the default catalog or a user-supplied JSON file.</summary>
    public static CatalogFile LoadFile(string? path = null)
    {
        var file = path ?? Env.Get(Env.Catalog) ?? DefaultPath();
        using var doc = JsonDocument.Parse(File.ReadAllText(file), new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip });
        var root = doc.RootElement;
        var providers = BuiltinProviders();
        List<ModelSpec> models;
        if (root.ValueKind == JsonValueKind.Array) models = root.Deserialize<List<ModelSpec>>(Json.Camel) ?? [];
        else
        {
            models = root.TryGetProperty("models", out var m) ? m.Deserialize<List<ModelSpec>>(Json.Camel) ?? [] : [];
            if (root.TryGetProperty("providers", out var p))
                foreach (var (name, cfg) in p.Deserialize<Dictionary<string, ProviderConfig>>(Json.Camel) ?? [])
                    providers[name] = cfg;
        }
        foreach (var (name, cfg) in providers)
            if (string.IsNullOrEmpty(cfg.BaseUrl)) throw new InvalidOperationException($"catalog: provider \"{name}\" needs a baseUrl");
        foreach (var m in models) Validate(m, providers);
        return new CatalogFile { Providers = providers, Models = models };
    }

    /// <summary>Convenience: just the models.</summary>
    public static List<ModelSpec> Load(string? path = null) => LoadFile(path).Models;

    private static void Validate(ModelSpec m, Dictionary<string, ProviderConfig> providers)
    {
        void Bad(string msg) => throw new InvalidOperationException($"catalog: model \"{m.Id}\": {msg}");
        if (string.IsNullOrEmpty(m.Id) || string.IsNullOrEmpty(m.Provider) || string.IsNullOrEmpty(m.Model)) Bad("id, provider and model are required");
        if (!providers.ContainsKey(m.Provider)) Bad($"unknown provider \"{m.Provider}\"; declare it under \"providers\"");
        if (m.Tier is < 1 or > 3) Bad("tier must be 1, 2 or 3");
        if (m.Price.Input < 0 || m.Price.Output < 0) Bad("price.input/output required");
        foreach (var e in m.Effort) if (!Efforts.IsValid(e)) Bad($"unknown effort \"{e}\"");
        foreach (var t in (m.Strengths ?? []).Concat(m.Weaknesses ?? [])) if (!Tasks.All.Contains(t)) Bad($"unknown task \"{t}\"");
    }

    /// <summary>A provider is usable when it is enabled and either needs no key or its key is present.</summary>
    public static bool ProviderAvailable(ProviderConfig? p)
    {
        if (p is null || p.Enabled == false) return false;
        return string.IsNullOrEmpty(p.ApiKeyEnv) || Env.Has(p.ApiKeyEnv);
    }

    /// <summary>Models whose provider is usable right now.</summary>
    public static List<ModelSpec> Available(IEnumerable<ModelSpec> catalog, Dictionary<string, ProviderConfig>? providers = null)
    {
        providers ??= BuiltinProviders();
        return catalog.Where(m => ProviderAvailable(providers.GetValueOrDefault(m.Provider))).ToList();
    }

    public static ModelSpec? Find(IEnumerable<ModelSpec> catalog, string name) =>
        catalog.FirstOrDefault(m =>
            string.Equals(m.Id, name, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(m.Model, name, StringComparison.OrdinalIgnoreCase));
}
