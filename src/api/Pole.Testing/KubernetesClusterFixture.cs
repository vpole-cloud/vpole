// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

using k8s;
using Xunit;

namespace Pole.Testing;

public sealed class KubernetesClusterFixture : IAsyncLifetime
{
    private KubernetesLabState? _state;
    private KubernetesLabTarget? _target;

    public IKubernetes Client { get; private set; } = null!;

    public KubernetesLabTarget Target =>
        _target ?? throw new InvalidOperationException("The Kubernetes lab fixture is not initialized.");

    public KubernetesLabState State =>
        _state ?? throw new InvalidOperationException("The Kubernetes lab fixture is not initialized.");

    public TestRunIdentity Identity { get; } = TestRunIdentity.Create();

    public async Task InitializeAsync()
    {
        var selection = KubernetesLabSelection.RequireSelection();
        _target = selection.Target;
        _state = selection.State;
        Client = new Kubernetes(_target.CreateConfiguration());
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await Client.CoreV1.ListNamespaceAsync(cancellationToken: timeout.Token);
    }

    public Task DisposeAsync()
    {
        Client?.Dispose();
        return Task.CompletedTask;
    }
}
