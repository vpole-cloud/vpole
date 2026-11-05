using System.Diagnostics;
using System.Text;
using Pole.Features.SourceBuilds.Domain;

namespace Pole.Features.SourceBuilds.Application.Execution.ContainerBuild;

public sealed record ProcessSpec(
    string FileName,
    IReadOnlyList<string> Arguments,
    string WorkingDirectory,
    IReadOnlyDictionary<string, string> EnvironmentVariables,
    int MaxCapturedCharacters = 64 * 1024,
    Func<SourceBuildLogStream, string, CancellationToken, Task>? Output = null);

public sealed record ProcessResult(
    int ExitCode,
    string StandardOutput,
    string StandardError,
    bool OutputCaptureFailed = false);

public delegate Task<ProcessResult> RunProcess(
    ProcessSpec spec,
    CancellationToken cancellationToken = default);

public sealed class ProcessRunner
{
    public async Task<ProcessResult> InvokeAsync(
        ProcessSpec spec,
        CancellationToken cancellationToken = default)
    {
        if (spec.MaxCapturedCharacters is < 0 or > 1024 * 1024)
            throw new ArgumentOutOfRangeException(nameof(spec.MaxCapturedCharacters));
        var startInfo = new ProcessStartInfo
        {
            FileName = spec.FileName,
            WorkingDirectory = spec.WorkingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (var argument in spec.Arguments)
            startInfo.ArgumentList.Add(argument);
        foreach (var (name, value) in spec.EnvironmentVariables)
            startInfo.Environment[name] = value;

        using var process = new Process { StartInfo = startInfo };
        process.Start();
        var stdout = ReadBoundedAsync(process.StandardOutput, spec.MaxCapturedCharacters, SourceBuildLogStream.StandardOutput, spec.Output, cancellationToken);
        var stderr = ReadBoundedAsync(process.StandardError, spec.MaxCapturedCharacters, SourceBuildLogStream.StandardError, spec.Output, cancellationToken);
        try
        {
            await process.WaitForExitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync(CancellationToken.None);
            await Task.WhenAll(stdout, stderr);
            throw;
        }

        var output = await stdout;
        var error = await stderr;
        return new ProcessResult(process.ExitCode, output.Text, error.Text, output.Failed || error.Failed);
    }

    private static async Task<(string Text, bool Failed)> ReadBoundedAsync(StreamReader reader, int maxCharacters, SourceBuildLogStream stream, Func<SourceBuildLogStream, string, CancellationToken, Task>? sink, CancellationToken cancellationToken)
    {
        var output = new StringBuilder(Math.Min(maxCharacters, 4096));
        var buffer = new char[4096];
        var failed = false;
        while (await reader.ReadAsync(buffer) is var read && read > 0)
        {
            var remaining = maxCharacters - output.Length;
            if (remaining > 0)
                output.Append(buffer, 0, Math.Min(read, remaining));
            if (sink is not null && !failed)
            {
                try { await sink(stream, new string(buffer, 0, read), cancellationToken); }
                catch (Exception) { failed = true; } // Keep draining pipes even when diagnostics storage fails.
            }
        }

        return (output.ToString(), failed);
    }
}
