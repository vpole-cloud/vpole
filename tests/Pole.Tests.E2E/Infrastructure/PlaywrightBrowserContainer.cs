// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

using Docker.DotNet;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using DotNet.Testcontainers.Images;
using Pole.Testing;

namespace Pole.Tests.E2E.Infrastructure;

internal sealed class PlaywrightBrowserContainer : IAsyncDisposable
{
    private const int PlaywrightPort = 3000;
    private const string PlaywrightVersion = "1.64.1";
    private readonly IContainer _container;

    public PlaywrightBrowserContainer(TestRunIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(identity);
        _container = new ContainerBuilder($"mcr.microsoft.com/playwright:v{PlaywrightVersion}-noble")
            .WithTestOwnership(identity)
            .WithImagePullPolicy(PullPolicy.Missing)
            .WithEntrypoint("/bin/sh", "-c")
            .WithCommand($"npx -y playwright@{PlaywrightVersion} run-server --port {PlaywrightPort} --host 0.0.0.0")
            .WithPortBinding(PlaywrightPort, assignRandomHostPort: true)
            .WithCreateParameterModifier(parameters => parameters.HostConfig!.IpcMode = "host")
            .WithWaitStrategy(
                Wait.ForUnixContainer()
                    .UntilHttpRequestIsSucceeded(request => request.ForPort(PlaywrightPort).ForPath("/json")))
            .Build();
    }

    public string WebSocketEndpoint => $"ws://{_container.Hostname}:{_container.GetMappedPublicPort(PlaywrightPort)}/";

    public ValueTask DisposeAsync() => _container.DisposeAsync();

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        const int maximumAttempts = 3;
        for (var attempt = 1;; attempt++)
            try
            {
                await _container.StartAsync(cancellationToken);
                return;
            }
            catch (DockerApiException exception) when (attempt < maximumAttempts &&
                                                       !cancellationToken.IsCancellationRequested &&
                                                       IsTransientImagePullFailure(exception))
            {
                await Task.Delay(TimeSpan.FromSeconds(attempt * 2), cancellationToken);
            }
    }

    private static bool IsTransientImagePullFailure(DockerApiException exception) =>
        exception.Message.Contains("TLS handshake timeout", StringComparison.OrdinalIgnoreCase) ||
        exception.Message.Contains("i/o timeout", StringComparison.OrdinalIgnoreCase) ||
        exception.Message.Contains("connection reset", StringComparison.OrdinalIgnoreCase) ||
        exception.Message.Contains("unexpected EOF", StringComparison.OrdinalIgnoreCase);
}
