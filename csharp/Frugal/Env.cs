namespace Frugal;

/// <summary>
/// Loads a <c>.env</c> file into the process environment without overriding variables that are
/// already set. Looks in the working directory, then up to three parents. Values are never logged.
/// </summary>
public static class Env
{
    public const string TypeSafe = "TYPESAFE_API_KEY";
    public const string Anthropic = "ANTHROPIC_API_KEY";
    public const string OpenAI = "OPENAI_API_KEY";
    public const string OpenAIBaseUrl = "OPENAI_BASE_URL";
    public const string OpenRouter = "OPENROUTER_API_KEY";
    public const string Port = "FRUGAL_PORT";
    public const string ApiKey = "FRUGAL_API_KEY";
    public const string Baseline = "FRUGAL_BASELINE";
    public const string Fallback = "FRUGAL_FALLBACK";
    public const string RouteAll = "FRUGAL_ROUTE_ALL";
    public const string LedgerPath = "FRUGAL_LEDGER_PATH";
    public const string Catalog = "FRUGAL_CATALOG";
    public const string JevTimeout = "FRUGAL_JEV_TIMEOUT_MS";

    public static bool Load(string? file = null)
    {
        file ??= Environment.GetEnvironmentVariable("FRUGAL_ENV_FILE") ?? ".env";
        var dir = Directory.GetCurrentDirectory();
        for (var i = 0; i < 4 && dir is not null; i++)
        {
            var path = Path.Combine(dir, file);
            if (File.Exists(path))
            {
                Apply(File.ReadAllLines(path));
                return true;
            }
            dir = Path.GetDirectoryName(dir);
        }
        return false;
    }

    internal static void Apply(IEnumerable<string> lines)
    {
        foreach (var raw in lines)
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;
            if (line.StartsWith("export ", StringComparison.Ordinal)) line = line[7..].TrimStart();
            var eq = line.IndexOf('=');
            if (eq <= 0) continue;
            var key = line[..eq].Trim();
            var value = line[(eq + 1)..].Trim();
            if (value.Length >= 2 && (value[0] == '"' && value[^1] == '"' || value[0] == '\'' && value[^1] == '\''))
                value = value[1..^1];
            else
            {
                var hash = value.IndexOf(" #", StringComparison.Ordinal);
                if (hash >= 0) value = value[..hash].TrimEnd();
            }
            if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable(key)))
                Environment.SetEnvironmentVariable(key, value);
        }
    }

    /// <summary>Presence only. Never returns or prints the value.</summary>
    public static bool Has(string name) => !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(name));

    public static string? Get(string name) => Environment.GetEnvironmentVariable(name);
}
