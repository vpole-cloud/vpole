// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Pole.Testing;

public sealed class KubernetesPortForwardSession : IAsyncDisposable
{
    private readonly Process _process;
    private readonly CancellationTokenSource _readerLifetime;
    private readonly Task<string> _standardError;
    private readonly Task<string> _standardOutput;
    private int _disposed;

    private KubernetesPortForwardSession(
        Process process,
        int localPort,
        Task<string> standardOutput,
        Task<string> standardError,
        CancellationTokenSource readerLifetime)
    {
        _process = process;
        _readerLifetime = readerLifetime;
        LocalPort = localPort;
        _standardOutput = standardOutput;
        _standardError = standardError;
    }

    public int LocalPort { get; }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;
        try
        {
            await StopAsync();
        }
        finally
        {
            await _readerLifetime.CancelAsync();
            _process.Dispose();
            _readerLifetime.Dispose();
        }
    }

    public static async Task<KubernetesPortForwardSession> StartAsync(
        KubernetesLabTarget target,
        string namespaceName,
        string resource,
        int remotePort,
        CancellationToken cancellationToken = default,
        int? localPort = null)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentException.ThrowIfNullOrWhiteSpace(namespaceName);
        ArgumentException.ThrowIfNullOrWhiteSpace(resource);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(remotePort);

        var assignedPort = localPort ?? GetFreeTcpPort();
        ArgumentOutOfRangeException.ThrowIfLessThan(assignedPort, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(assignedPort, 65535);
        var startInfo = new ProcessStartInfo
        {
            FileName = target.KubectlPath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var argument in target.KubectlSelectionArguments)
            startInfo.ArgumentList.Add(argument);
        startInfo.ArgumentList.Add("--namespace");
        startInfo.ArgumentList.Add(namespaceName);
        startInfo.ArgumentList.Add("port-forward");
        startInfo.ArgumentList.Add(resource);
        startInfo.ArgumentList.Add($"{assignedPort}:{remotePort}");

        return await StartProcessAsync(startInfo, assignedPort, cancellationToken);
    }

    internal static async Task<KubernetesPortForwardSession> StartProcessAsync(
        ProcessStartInfo startInfo,
        int assignedPort,
        CancellationToken cancellationToken)
    {
        var process = Process.Start(startInfo) ??
            throw new InvalidOperationException("Failed to start kubectl port-forward process.");
        var lifetime = new CancellationTokenSource();
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var forwardingMessage = $"Forwarding from 127.0.0.1:{assignedPort} ->";
        // Startup cancellation must never stop draining the pipes of a running session.
        var standardOutput = ReadBoundedOutputAsync(
            process.StandardOutput,
            line =>
            {
                if (line.StartsWith(forwardingMessage, StringComparison.Ordinal))
                    ready.TrySetResult();
            },
            lifetime.Token);
        var standardError = ReadBoundedOutputAsync(process.StandardError, lifetime.Token);
        var session = new KubernetesPortForwardSession(process, assignedPort, standardOutput, standardError, lifetime);
        try
        {
            await WaitForForwardingAsync(process, ready.Task, cancellationToken);
            return session;
        }
        catch (Exception primaryFailure)
        {
            try
            {
                await session.DisposeAsync();
            }
            catch (Exception cleanupFailure)
            {
                throw new AggregateException(
                    "Port-forward startup and cleanup failed.",
                    primaryFailure,
                    cleanupFailure);
            }

            throw;
        }
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
        catch (Exception failure) when (failure is IOException or ObjectDisposedException)
        {
            return "<closed>";
        }
    }

    private static async Task<string> ReadBoundedOutputAsync(
        StreamReader reader,
        CancellationToken cancellationToken) => await ReadBoundedOutputAsync(reader, null, cancellationToken);

    private static async Task<string> ReadBoundedOutputAsync(
        StreamReader reader,
        Action<string>? observeLine,
        CancellationToken cancellationToken)
    {
        const int maximumCharacters = 4_096;
        var retained = new StringBuilder(maximumCharacters);
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

        return retained + (retained.Length == maximumCharacters ? "<truncated>" : "");
    }
}
