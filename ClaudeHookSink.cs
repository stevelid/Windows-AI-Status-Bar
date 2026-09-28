using System.Diagnostics;
using System.IO;
using System.Text;
using StatusBar.Core.Claude;

namespace ClaudeUsageWidget;

/// <summary>Handles Claude's short-lived hook process without initializing the widget UI.</summary>
internal static class ClaudeHookSink
{
    const int MaxInputBytes = 1024 * 1024;
    const int MaxOutputBytes = 256 * 1024;
    const int RetainedLines = 200;
    static readonly TimeSpan RuntimeLimit = TimeSpan.FromMilliseconds(900);
    static readonly TimeSpan ReadLimit = TimeSpan.FromMilliseconds(650);
    static readonly TimeSpan LockLimit = TimeSpan.FromMilliseconds(150);
    static readonly UTF8Encoding StrictUtf8 = new(false, true);

    internal static bool IsInvocation(string[] args) =>
        args.Any(argument => string.Equals(argument, "--claude-hook", StringComparison.OrdinalIgnoreCase));

    internal static async Task HandleAsync(Stream input)
    {
        ArgumentNullException.ThrowIfNull(input);
        var stopwatch = Stopwatch.StartNew();
        using var cancellation = new CancellationTokenSource();
        var readTask = ReadInputAsync(input, cancellation.Token);
        string? json;
        try
        {
            json = await readTask.WaitAsync(ReadLimit).ConfigureAwait(false);
        }
        catch
        {
            cancellation.Cancel();
            _ = readTask.ContinueWith(task => _ = task.Exception, TaskContinuationOptions.OnlyOnFaulted);
            return;
        }

        if (json is null) return;
        var line = ClaudeHookLine.FromHookJson(json);
        if (line is null || stopwatch.Elapsed >= RuntimeLimit) return;

        var writeBudget = RuntimeLimit - stopwatch.Elapsed;
        var writeTask = Task.Run(() => AppendLine(line, stopwatch, writeBudget));
        try
        {
            await writeTask.WaitAsync(writeBudget).ConfigureAwait(false);
        }
        catch
        {
            _ = writeTask.ContinueWith(task => _ = task.Exception, TaskContinuationOptions.OnlyOnFaulted);
            // Hook failures are intentionally silent so they cannot interrupt Claude Code.
        }
    }

    static async Task<string?> ReadInputAsync(Stream input, CancellationToken cancellationToken)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[8192];
        while (true)
        {
            var read = await input.ReadAsync(chunk.AsMemory(), cancellationToken).ConfigureAwait(false);
            if (read == 0) break;
            if (buffer.Length + read > MaxInputBytes) return null;
            buffer.Write(chunk, 0, read);
        }

        try
        {
            return StrictUtf8.GetString(buffer.ToArray());
        }
        catch (DecoderFallbackException)
        {
            return null;
        }
    }

    static void AppendLine(string line, Stopwatch stopwatch, TimeSpan writeBudget)
    {
        using var mutex = new Mutex(false, "Local\\WindowsAIStatusBar-ClaudeHookSink");
        var ownsMutex = false;
        try
        {
            try
            {
                ownsMutex = mutex.WaitOne(writeBudget < LockLimit ? writeBudget : LockLimit);
            }
            catch (AbandonedMutexException)
            {
                ownsMutex = true;
            }

            if (!ownsMutex || stopwatch.Elapsed >= RuntimeLimit) return;

            var directory = AppPaths.DataDir;
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, "claude-hooks.jsonl");
            var bytes = Encoding.UTF8.GetBytes(line + "\n");
            using (var output = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete))
                output.Write(bytes);

            if (new FileInfo(path).Length > MaxOutputBytes)
                KeepLastLines(path);
        }
        finally
        {
            if (ownsMutex) mutex.ReleaseMutex();
        }
    }

    static void KeepLastLines(string path)
    {
        var retained = File.ReadLines(path).TakeLast(RetainedLines).ToArray();
        var temporaryPath = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllLines(temporaryPath, retained, new UTF8Encoding(false));
            File.Move(temporaryPath, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
        }
    }
}
