using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using StatusBar.Core.Projects;

namespace ClaudeUsageWidget;

/// <summary>Reads bounded evidence using the installed Claude subscription and latest Haiku alias.</summary>
internal sealed class ClaudeCliProjectSummaryProvider(Func<string?>? resolveExecutable = null) : IProjectSummaryProvider
{
    static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true, MaxDepth = 16 };

    public async Task<ProjectSummaryResult?> SummarizeAsync(ProjectSummaryRequest request, CancellationToken cancellationToken)
    {
        if (!ProjectSummaryValidator.IsValidRequest(request)) throw new InvalidDataException("SummaryInputInvalid");
        var prompt = ProjectSummaryPrompt.Build(request);
        if (Encoding.UTF8.GetByteCount(prompt) > 12 * 1024) throw new InvalidDataException("SummaryInputTooLarge");
        var executable = resolveExecutable?.Invoke() ?? Locate();
        if (executable is null || !File.Exists(executable) || !executable.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            throw Failure("ClaudeCliUnavailable");
        var neutralDirectory = Path.Combine(Path.GetTempPath(), "windows-ai-status-bar-summary", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(neutralDirectory);
        try
        {
            var authStart = StartInfo(executable, neutralDirectory, "auth", "status");
            var auth = await RunAsync(authStart, null, TimeSpan.FromSeconds(10), 8192, cancellationToken);
            if (auth.ExitCode != 0 || !IsSubscriptionLogin(auth.Output)) throw Failure("ClaudeSubscriptionUnavailable");
            var start = StartInfo(executable, neutralDirectory, "--print", "--model", "haiku", "--output-format", "json",
                "--json-schema", ProjectSummaryPrompt.OutputSchema, "--no-session-persistence", "--safe-mode", "--restricted",
                "--tools", "", "--disallowedTools", "mcp__*", "--strict-mcp-config", "--mcp-config", "{\"mcpServers\":{}}",
                "--settings", "{\"disableAllHooks\":true}", "--setting-sources", "", "--disable-slash-commands", "--no-chrome");
            var response = await RunAsync(start, prompt, TimeSpan.FromSeconds(60), 96 * 1024, cancellationToken);
            if (response.ExitCode != 0) throw Failure("ClaudeSummaryUnavailable");
            var result = Parse(response.Output) ?? throw new InvalidDataException("ClaudeSummaryOutputInvalid");
            if (!ProjectSummaryValidator.TryValidate(request, result)) throw new InvalidDataException("ClaudeSummaryOutputInvalid");
            return result;
        }
        finally
        {
            // Only the verified task-owned neutral directory is removed; it contains no saved source text.
            try { Directory.Delete(neutralDirectory, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    static ProjectSummaryProviderException Failure(string code) => new(ProjectSummaryProviderFailureKind.Unavailable, code);

    internal static bool IsSubscriptionLogin(string output)
    {
        try
        {
            using var json = JsonDocument.Parse(output);
            return json.RootElement.TryGetProperty("loggedIn", out var loggedIn) && loggedIn.ValueKind == JsonValueKind.True &&
                   json.RootElement.TryGetProperty("authMethod", out var method) && method.ValueKind == JsonValueKind.String &&
                   method.GetString() == "claude.ai";
        }
        catch (JsonException) { return false; }
    }

    internal static ProjectSummaryResult? Parse(string output)
    {
        try
        {
            using var json = JsonDocument.Parse(output);
            var root = json.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                root.TryGetProperty("is_error", out var error) && error.ValueKind == JsonValueKind.True) return null;
            ProjectSummaryResult? result = null;
            if (root.TryGetProperty("structured_output", out var structured) && structured.ValueKind == JsonValueKind.Object)
                result = structured.Deserialize<ProjectSummaryResult>(JsonOptions);
            else if (root.TryGetProperty("result", out var text) && text.ValueKind == JsonValueKind.String)
                result = JsonSerializer.Deserialize<ProjectSummaryResult>(text.GetString()!, JsonOptions);
            var model = "haiku";
            if (root.TryGetProperty("modelUsage", out var usage) && usage.ValueKind == JsonValueKind.Object)
            {
                var names = usage.EnumerateObject().Select(property => property.Name).ToArray();
                if (names.Any(name => !name.StartsWith("claude-haiku-", StringComparison.Ordinal))) return null;
                if (names.Length == 1 && names[0].Length <= 64 && names[0].All(c => char.IsAsciiLetterOrDigit(c) || c == '-'))
                    model = names[0];
            }
            return result is null ? null : result with { Model = model };
        }
        catch (JsonException) { return null; }
    }

    static string? Locate()
    {
        var candidates = new[] { Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "bin", "claude.exe") }
            .Concat((Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
                .Select(directory => Path.Combine(directory, "claude.exe")));
        return candidates.FirstOrDefault(File.Exists);
    }

    static ProcessStartInfo StartInfo(string executable, string directory, params string[] arguments)
    {
        var start = new ProcessStartInfo(executable)
        {
            WorkingDirectory = directory, UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false), StandardOutputEncoding = new UTF8Encoding(false), StandardErrorEncoding = new UTF8Encoding(false),
        };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        foreach (var key in start.Environment.Keys.Where(key => key.StartsWith("ANTHROPIC_", StringComparison.OrdinalIgnoreCase) ||
                     key.StartsWith("CLAUDE_CODE_USE_", StringComparison.OrdinalIgnoreCase) ||
                     key is "CLAUDE_CODE_OAUTH_TOKEN" or "CLAUDE_CODE_API_KEY" or "CLAUDE_CODE_DEBUG_LOG" or "CLAUDECODE" or "CLAUDE_CODE_ENTRYPOINT").ToArray()) start.Environment.Remove(key);
        start.Environment["CLAUDE_CODE_SKIP_PROMPT_HISTORY"] = "1";
        start.Environment["CLAUDE_CODE_DISABLE_NONESSENTIAL_TRAFFIC"] = "1";
        return start;
    }

    static async Task<(int ExitCode, string Output)> RunAsync(ProcessStartInfo start, string? input, TimeSpan duration, int maximum, CancellationToken token)
    {
        using var process = new Process { StartInfo = start };
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(duration);
        try
        {
            if (!process.Start()) throw Failure("ClaudeCliStartFailed");
            var output = ReadAsync(process.StandardOutput, maximum, timeout.Token);
            var errors = ReadAsync(process.StandardError, 0, timeout.Token);
            if (input is not null) await process.StandardInput.WriteAsync(input.AsMemory(), timeout.Token);
            process.StandardInput.Close();
            await process.WaitForExitAsync(timeout.Token);
            var result = await output;
            await errors;
            if (result.TooLarge) throw new InvalidDataException("ClaudeSummaryOutputTooLarge");
            return (process.ExitCode, result.Text);
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            Kill(process);
            throw new TimeoutException("ClaudeSummaryTimeout");
        }
        catch (System.ComponentModel.Win32Exception) { Kill(process); throw Failure("ClaudeCliStartFailed"); }
        catch { Kill(process); throw; }
    }

    static async Task<(string Text, bool TooLarge)> ReadAsync(StreamReader reader, int maximum, CancellationToken token)
    {
        var buffer = new char[4096];
        var text = new StringBuilder();
        var tooLarge = false;
        int count;
        while ((count = await reader.ReadAsync(buffer.AsMemory(), token)) > 0)
        {
            var keep = Math.Min(count, Math.Max(0, maximum - text.Length));
            text.Append(buffer, 0, keep);
            if (maximum > 0 && keep != count) tooLarge = true;
        }
        return (text.ToString(), tooLarge);
    }

    static void Kill(Process process)
    {
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        catch (InvalidOperationException) { }
        catch (System.ComponentModel.Win32Exception) { }
    }
}
