using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

namespace Pole.Testing;

public sealed class KubernetesPortForwardSession : IAsyncDisposable
{
    private readonly Process _process;
    private readonly Task<string> _standardOutput;
    private readonly Task<string> _standardError;

    private KubernetesPortForwardSession(
        Process process,
        int localPort,
        Task<string> standardOutput,
        Task<string> standardError)
    {
        _process = process;
        LocalPort = localPort;
        _standardOutput = standardOutput;
        _standardError = standardError;
    }

    public int LocalPort { get; }

    public static async Task<KubernetesPortForwardSession> StartAsync(
        KubernetesLabTarget target,
        string namespaceName,
        string resource,
        int remotePort,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentException.ThrowIfNullOrWhiteSpace(namespaceName);
        ArgumentException.ThrowIfNullOrWhiteSpace(resource);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(remotePort);

        var localPort = GetFreeTcpPort();
        var startInfo = new ProcessStartInfo
        {
            FileName = target.KubectlPath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        foreach (var argument in target.KubectlSelectionArguments)
            startInfo.ArgumentList.Add(argument);
        startInfo.ArgumentList.Add("--namespace");
        startInfo.ArgumentList.Add(namespaceName);
        startInfo.ArgumentList.Add("port-forward");
        startInfo.ArgumentList.Add(resource);
        startInfo.ArgumentList.Add($"{localPort}:{remotePort}");

        var process = Process.Start(startInfo)
                      ?? throw new InvalidOperationException("Failed to start kubectl port-forward process.");
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var forwardingMessage = $"Forwarding from 127.0.0.1:{localPort} ->";
        var standardOutput = ReadBoundedOutputAsync(
            process.StandardOutput,
            line =>
            {
                if (line.StartsWith(forwardingMessage, StringComparison.Ordinal))
                    ready.TrySetResult();
            },
            cancellationToken);
        var standardError = ReadBoundedOutputAsync(process.StandardError, cancellationToken);
        var session = new KubernetesPortForwardSession(process, localPort, standardOutput, standardError);

        try
        {
            await WaitForForwardingAsync(process, ready.Task, cancellationToken);
            return session;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await session.StopAsync();
            throw;
        }
        catch (Exception exception)
        {
            await session.StopAsync();
            var output = await ReadOutputAsync(standardOutput);
            var error = await ReadOutputAsync(standardError);
            throw new TimeoutException(
                $"kubectl port-forward for '{resource}' on target '{target.Key}' did not become ready. " +
                $"stdout: {output} stderr: {error}",
                exception);
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        _process.Dispose();
    }

    private async Task StopAsync()
    {
        try
        {
            if (!_process.HasExited)
                _process.Kill(entireProcessTree: true);
            await _process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
        }
        catch (InvalidOperationException)
        {
        }
        catch (TimeoutException)
        {
            _process.StandardOutput.Close();
            _process.StandardError.Close();
        }

        try
        {
            await Task.WhenAll(ReadOutputAsync(_standardOutput), ReadOutputAsync(_standardError))
                .WaitAsync(TimeSpan.FromSeconds(2));
        }
        catch (TimeoutException)
        {
            _process.StandardOutput.Close();
            _process.StandardError.Close();
        }
    }

    private static async Task WaitForForwardingAsync(
        Process process,
        Task ready,
        CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        var processExited = process.WaitForExitAsync(timeout.Token);
        var completed = await Task.WhenAny(ready, processExited);

        if (completed == ready)
        {
            if (process.HasExited)
                throw new InvalidOperationException($"kubectl port-forward exited with code {process.ExitCode}.");
            return;
        }

        await processExited;
        throw new InvalidOperationException($"kubectl port-forward exited with code {process.ExitCode}.");
    }

    private static int GetFreeTcpPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static async Task<string> ReadOutputAsync(Task<string> output)
    {
        try
        {
            return await output;
        }
        catch (OperationCanceledException)
        {
            return "<cancelled>";
        }
    }

    private static async Task<string> ReadBoundedOutputAsync(
        StreamReader reader,
        CancellationToken cancellationToken) =>
        await ReadBoundedOutputAsync(reader, null, cancellationToken);

    private static async Task<string> ReadBoundedOutputAsync(
        StreamReader reader,
        Action<string>? observeLine,
        CancellationToken cancellationToken)
    {
        const int maximumCharacters = 4_096;
        var retained = new System.Text.StringBuilder(maximumCharacters);
        while (true)
        {
            var line = await reader.ReadLineAsync(cancellationToken);
            if (line is null)
                break;

            observeLine?.Invoke(line);
            if (retained.Length < maximumCharacters)
            {
                var copy = Math.Min(line.Length, maximumCharacters - retained.Length);
                retained.Append(line.AsSpan(0, copy));
                if (retained.Length < maximumCharacters)
                    retained.AppendLine();
            }
        }

        return retained.ToString() +
               (retained.Length == maximumCharacters ? "<truncated>" : string.Empty);
    }
}
