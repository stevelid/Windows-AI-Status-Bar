using System.Diagnostics;
using System.IO;
using System.Text;

namespace ClaudeUsageWidget;

/// <summary>Runs the signed-in Codex CLI without exposing output or errors to application logs.</summary>
internal sealed class CodexCloudTaskClient(Func<string?> configuredPath, string? codexHome)
{
    /// <summary>Reads one cloud page. All task data stays in memory.</summary>
    public async Task<string> ReadPageAsync(string? cursor, CancellationToken cancellationToken)
    {
        var executable = CodexLocator.Resolve(configuredPath());
        var extension = Path.GetExtension(executable);
        // A native executable avoids forwarding opaque pagination data through a command shell.
        if (extension.Equals(".cmd", StringComparison.OrdinalIgnoreCase) ||
            extension.Equals(".bat", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Cloud collector requires native Codex.");

        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var argument in new[] { "cloud", "list", "--json", "--limit", "20" })
            start.ArgumentList.Add(argument);
        if (cursor is not null)
        {
            start.ArgumentList.Add("--cursor");
            start.ArgumentList.Add(cursor);
        }
        if (!string.IsNullOrWhiteSpace(codexHome)) start.Environment["CODEX_HOME"] = codexHome;

        // The experimental CLI can write error.log to its current directory (A-X9).
        // Isolate and delete it rather than leaving account details in the app or repo folder.
        var scratch = Path.Combine(Path.GetTempPath(), "statusbar-cloud-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(scratch);
        start.WorkingDirectory = scratch;
        try
        {
            using var process = new Process { StartInfo = start };
            process.Start();
            using var registration = cancellationToken.Register(() =>
            {
                try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
                catch (InvalidOperationException) { }
                catch (System.ComponentModel.Win32Exception) { }
            });
            var output = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var error = process.StandardError.ReadToEndAsync(cancellationToken);
            try
            {
                await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
                await error.ConfigureAwait(false);
                var json = await output.ConfigureAwait(false);
                if (process.ExitCode != 0) throw new InvalidOperationException("Cloud list failed.");
                return json;
            }
            finally
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                    await process.WaitForExitAsync().ConfigureAwait(false);
                }
                // Observe both readers even when the process times out.
                try { await Task.WhenAll(output, error).ConfigureAwait(false); }
                catch (OperationCanceledException) { }
            }
        }
        finally
        {
            try
            {
                File.Delete(Path.Combine(scratch, "error.log"));
                Directory.Delete(scratch);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
