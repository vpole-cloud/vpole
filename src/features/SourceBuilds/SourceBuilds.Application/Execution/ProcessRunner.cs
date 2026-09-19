using System.Diagnostics;
using System.Text;

namespace Pole.Features.SourceBuilds.Application.Execution;

public sealed record ProcessSpec(
    string FileName,
    IReadOnlyList<string> Arguments,
    string WorkingDirectory,
    IReadOnlyDictionary<string, string> EnvironmentVariables,
    int MaxCapturedCharacters = 64 * 1024);

public sealed record ProcessResult(
    int ExitCode,
    string StandardOutput,
    string StandardError);

public delegate Task<ProcessResult> RunProcess(
    ProcessSpec spec,
    CancellationToken cancellationToken = default);

public sealed class ProcessRunner
{
    public async Task<ProcessResult> InvokeAsync(
        ProcessSpec spec,
        CancellationToken cancellationToken = default)
    {
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
        var stdout = ReadBoundedAsync(process.StandardOutput, spec.MaxCapturedCharacters);
        var stderr = ReadBoundedAsync(process.StandardError, spec.MaxCapturedCharacters);
        try
        {
            await process.WaitForExitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
            throw;
        }

        return new ProcessResult(process.ExitCode, await stdout, await stderr);
    }

    private static async Task<string> ReadBoundedAsync(StreamReader reader, int maxCharacters)
    {
        var output = new StringBuilder(Math.Min(maxCharacters, 4096));
        var buffer = new char[4096];
        while (await reader.ReadAsync(buffer) is var read && read > 0)
        {
            var remaining = maxCharacters - output.Length;
            if (remaining > 0)
                output.Append(buffer, 0, Math.Min(read, remaining));
        }

        return output.ToString();
    }
}
