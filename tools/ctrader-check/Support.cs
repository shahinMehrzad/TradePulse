using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Configuration.UserSecrets;

namespace CtraderCheck;

internal sealed record CliOptions(string Command, Dictionary<string, string> Values, HashSet<string> Flags)
{
    public string Get(string name, string fallback) => Values.TryGetValue(name, out var value) ? value : fallback;

    public bool Has(string flag) => Flags.Contains(flag);

    public static CliOptions Parse(string[] args)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var flags = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        for (var i = 1; i < args.Length; i++)
        {
            if (!args[i].StartsWith("--", StringComparison.Ordinal))
                throw new ArgumentException($"Unexpected argument {args[i]}.");

            var name = args[i][2..];
            if (name == "print")
                flags.Add(name);
            else if (i + 1 < args.Length)
                values[name] = args[++i];
            else
                throw new ArgumentException($"--{name} needs a value.");
        }

        return new CliOptions(args.Length > 0 ? args[0] : string.Empty, values, flags);
    }
}

/// <summary>The "dotnet user-secrets" files, read and written directly so that a secret never goes through a command line.</summary>
internal static class Secrets
{
    // The ids of the programs that read the settings (their csproj files). The collector and the API are the ones that hold the access token.
    public const string Collector = "2c4d9591-0361-4249-b341-098d344e3bac";
    public const string Api = "c1e434ca-ab18-4acb-b2e1-73d2e7029f93";
    public const string ThisTool = "3a7e5c10-84b2-4f6d-a1c9-5e0d2b7f9c34";

    public static string? Get(string secretsId, string key) => Load(secretsId)[key]?.GetValue<string>();

    public static void Set(string secretsId, IReadOnlyDictionary<string, string> values)
    {
        var path = PathHelper.GetSecretsPathFromSecretsId(secretsId);
        var all = Load(secretsId);
        foreach (var (key, value) in values)
            all[key] = value;

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, all.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }

    private static JsonObject Load(string secretsId)
    {
        var path = PathHelper.GetSecretsPathFromSecretsId(secretsId);
        return File.Exists(path) && JsonNode.Parse(File.ReadAllText(path)) is JsonObject existing ? existing : new JsonObject();
    }
}

internal static class Prompt
{
    public static string ClientId() => Environment.GetEnvironmentVariable("CTRADER_CLIENT_ID") is { Length: > 0 } id ? id : Ask("Client ID: ", secret: false);

    public static string ClientSecret() =>
        Environment.GetEnvironmentVariable("CTRADER_CLIENT_SECRET") is { Length: > 0 } secret ? secret : Ask("Client secret (not shown): ", secret: true);

    private static string Ask(string question, bool secret)
    {
        if (Console.IsInputRedirected)
            throw new InvalidOperationException("Nothing to read from: set CTRADER_CLIENT_ID and CTRADER_CLIENT_SECRET in the environment.");

        Console.Write(question);
        if (!secret)
            return Console.ReadLine()?.Trim() ?? string.Empty;

        var typed = new System.Text.StringBuilder();
        for (var key = Console.ReadKey(intercept: true); key.Key != ConsoleKey.Enter; key = Console.ReadKey(intercept: true))
        {
            if (key.Key == ConsoleKey.Backspace && typed.Length > 0)
                typed.Length--;
            else if (!char.IsControl(key.KeyChar))
                typed.Append(key.KeyChar);
        }

        Console.WriteLine();
        return typed.ToString();
    }
}
