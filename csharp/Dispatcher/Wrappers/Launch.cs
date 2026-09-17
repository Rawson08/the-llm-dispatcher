using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace LlmDispatcher.Wrappers;

/// <summary>Finding and running the real CLI with inherited stdio.</summary>
public static class Launch
{
    /// <summary>Provider credentials that would make a CLI bill an API key instead of its own login.</summary>
    private static readonly string[] CredentialVars =
        ["ANTHROPIC_API_KEY", "ANTHROPIC_AUTH_TOKEN", "OPENAI_API_KEY", "OPENROUTER_API_KEY", "TYPESAFE_API_KEY"];

    public sealed record Resolved(string File, string[] Prefix, bool Shell);

    /// <summary>Find a CLI on PATH, handling Windows shims (.exe, .cmd, .bat, .ps1).</summary>
    public static Resolved? ResolveOnPath(string name)
    {
        var win = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
        string[] exts = win ? [".exe", ".cmd", ".bat", ".ps1", ""] : [""];
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(win ? ';' : ':', StringSplitOptions.RemoveEmptyEntries))
        {
            foreach (var ext in exts)
            {
                var file = Path.Combine(dir.Trim('"'), name + ext);
                if (!File.Exists(file)) continue;
                if (file.EndsWith(".ps1", StringComparison.OrdinalIgnoreCase))
                    return new Resolved("powershell.exe", ["-NoProfile", "-ExecutionPolicy", "Bypass", "-File", file], false);
                var shell = file.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase) || file.EndsWith(".bat", StringComparison.OrdinalIgnoreCase);
                return new Resolved(file, [], shell);
            }
        }
        return null;
    }

    /// <summary>
    /// Environment for the child: the parent's, minus every credential the dispatcher itself loaded
    /// from <c>.env</c>. A key the user exported in their own shell is their explicit choice and stays.
    /// </summary>
    public static void ApplyChildEnv(ProcessStartInfo psi, IReadOnlyDictionary<string, string> extra)
    {
        foreach (var k in CredentialVars) if (Env.LoadedFromEnvFile.Contains(k)) psi.Environment.Remove(k);
        foreach (var (k, v) in extra) psi.Environment[k] = v;
    }

    /// <summary>cmd.exe joins arguments verbatim, so anything with whitespace or metacharacters is quoted.</summary>
    public static string QuoteForShell(string arg)
    {
        if (arg.Length > 0 && arg.All(c => !char.IsWhiteSpace(c) && "\"&|<>^()%!".IndexOf(c) < 0)) return arg;
        var sb = new StringBuilder("\"");
        var backslashes = 0;
        foreach (var c in arg)
        {
            if (c == '\\') { backslashes++; continue; }
            if (c == '"') { sb.Append('\\', backslashes * 2 + 1).Append('"'); backslashes = 0; continue; }
            sb.Append('\\', backslashes).Append(c == '%' ? "%%" : c.ToString());
            backslashes = 0;
        }
        return sb.Append('\\', backslashes).Append('"').ToString();
    }

    /// <summary>Run the CLI with inherited stdio and return its exit code.</summary>
    public static async Task<int> RunChildAsync(Resolved cmd, IReadOnlyList<string> args, IReadOnlyDictionary<string, string> extraEnv, CancellationToken ct = default)
    {
        var psi = new ProcessStartInfo { UseShellExecute = false };
        var all = cmd.Prefix.Concat(args).ToList();
        if (cmd.Shell)
        {
            psi.FileName = "cmd.exe";
            psi.Arguments = $"/d /s /c \"{QuoteForShell(cmd.File)} {string.Join(' ', all.Select(QuoteForShell))}\"";
        }
        else
        {
            psi.FileName = cmd.File;
            foreach (var a in all) psi.ArgumentList.Add(a);
        }
        ApplyChildEnv(psi, extraEnv);
        using var proc = Process.Start(psi) ?? throw new InvalidOperationException($"could not start {cmd.File}");
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; /* let the child handle Ctrl+C */ };
        await proc.WaitForExitAsync(ct);
        return proc.ExitCode;
    }
}
