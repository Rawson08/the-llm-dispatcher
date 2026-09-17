using System.Text.Json;

namespace LlmDispatcher;

public static class Catalog
{
    private static readonly Dictionary<string, string> ProviderKey = new()
    {
        ["anthropic"] = Env.Anthropic,
        ["openai"] = Env.OpenAI,
        ["openrouter"] = Env.OpenRouter,
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

    public static List<ModelSpec> Load(string? path = null)
    {
        var file = path ?? Env.Get(Env.Catalog) ?? DefaultPath();
        using var doc = JsonDocument.Parse(File.ReadAllText(file), new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip });
        var root = doc.RootElement;
        var arr = root.ValueKind == JsonValueKind.Array ? root : root.GetProperty("models");
        var models = arr.Deserialize<List<ModelSpec>>(Json.Camel) ?? [];
        foreach (var m in models) Validate(m);
        return models;
    }

    private static void Validate(ModelSpec m)
    {
        void Bad(string msg) => throw new InvalidOperationException($"catalog: model \"{m.Id}\": {msg}");
        if (string.IsNullOrEmpty(m.Id) || string.IsNullOrEmpty(m.Provider) || string.IsNullOrEmpty(m.Model)) Bad("id, provider and model are required");
        if (m.Tier is < 1 or > 3) Bad("tier must be 1, 2 or 3");
        if (m.Price.Input < 0 || m.Price.Output < 0) Bad("price.input/output required");
        if (!ProviderKey.ContainsKey(m.Provider)) Bad($"unknown provider \"{m.Provider}\"");
        foreach (var e in m.Effort) if (!Efforts.IsValid(e)) Bad($"unknown effort \"{e}\"");
        foreach (var t in (m.Strengths ?? []).Concat(m.Weaknesses ?? [])) if (!Tasks.All.Contains(t)) Bad($"unknown task \"{t}\"");
    }

    /// <summary>Models whose provider has credentials configured.</summary>
    public static List<ModelSpec> Available(IEnumerable<ModelSpec> catalog) =>
        catalog.Where(m => Env.Has(ProviderKey[m.Provider])).ToList();

    public static ModelSpec? Find(IEnumerable<ModelSpec> catalog, string name) =>
        catalog.FirstOrDefault(m =>
            string.Equals(m.Id, name, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(m.Model, name, StringComparison.OrdinalIgnoreCase));
}
