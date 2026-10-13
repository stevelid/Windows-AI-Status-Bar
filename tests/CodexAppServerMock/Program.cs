using System.Text.Json;

if (args.Contains("--version", StringComparer.Ordinal))
{
    Console.WriteLine("codex-cli 0.159.0");
    return;
}

if (args.Length >= 2 && args[0] == "login" && args[1] == "status")
{
    Console.Error.WriteLine("Logged in with ChatGPT");
    return;
}

if (args.Contains("exec", StringComparer.Ordinal))
{
    _ = await Console.In.ReadToEndAsync();
    var schemaIndex = Array.IndexOf(args, "--output-schema");
    if (schemaIndex < 0 || schemaIndex + 1 >= args.Length || !File.Exists(args[schemaIndex + 1]) ||
        Environment.GetEnvironmentVariable("OPENAI_API_KEY") is not null ||
        Environment.GetEnvironmentVariable("CODEX_API_KEY") is not null ||
        Environment.GetEnvironmentVariable("OPENAI_BASE_URL") is not null)
    {
        Environment.ExitCode = 23;
        return;
    }

    var result = JsonSerializer.Serialize(new
    {
        suggestedProjectName = "Fictional sample project",
        latestAction = "Prepared a fictional sample",
        progress = Array.Empty<string>(),
        sourceEvidenceIds = new[] { "sample-run-turn:assistant" },
    });
    Console.WriteLine(JsonSerializer.Serialize(new
    {
        type = "item.completed",
        item = new { type = "agent_message", text = result },
    }));
    Console.WriteLine(JsonSerializer.Serialize(new { type = "turn.completed", usage = new { input_tokens = 1 } }));
    return;
}

while (Console.ReadLine() is { } line)
{
    using var document = JsonDocument.Parse(line);
    var root = document.RootElement;
    if (!root.TryGetProperty("id", out var id)) continue;
    var method = root.GetProperty("method").GetString();

    object result = method switch
    {
        "initialize" => new { userAgent = "mock", codexHome = "mock", platformFamily = "windows", platformOs = "windows" },
        "account/read" => new
        {
            account = new { type = "chatgpt", email = "private@example.invalid", planType = "plus" },
            requiresOpenaiAuth = true,
        },
        "account/rateLimits/read" => new
        {
            rateLimits = new
            {
                primary = new { usedPercent = 25.0, windowDurationMins = 300, resetsAt = 1_900_000_000L },
                secondary = new { usedPercent = 40.0, windowDurationMins = 10_080, resetsAt = 1_900_100_000L },
                rateLimitReachedType = (string?)null,
            },
            rateLimitResetCredits = (object?)null,
        },
        _ => new { },
    };

    Console.WriteLine(JsonSerializer.Serialize(new { id = id.GetInt64(), result }));
}
