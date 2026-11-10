using System.Net;
using k8s;
using k8s.Autorest;
using k8s.Models;
using Pole.Testing;

namespace Pole.Tests.Kubernetes;

internal sealed record OwnedNamespace(
    string Name,
    string Uid,
    TestRunIdentity Identity
);

internal static class OwnedNamespaceManager
{
    public static async Task<OwnedNamespace> CreateAsync(
        IKubernetes client,
        string name,
        TestRunIdentity identity,
        CancellationToken cancellationToken = default)
    {
        var created = await client.CoreV1.CreateNamespaceAsync(
            new V1Namespace
            {
                Metadata = new V1ObjectMeta
                {
                    Name = name,
                    Labels = new Dictionary<string, string>(identity.NamespaceLabels, StringComparer.Ordinal),
                },
            },
            cancellationToken: cancellationToken);
        return new OwnedNamespace(name, created.Metadata.Uid, identity);
    }

    public static Task<OwnedNamespace> CreateTrackedAsync(
        TestResourceCleanupGroup cleanupGroup,
        IKubernetes client,
        string name,
        TestRunIdentity identity,
        CancellationToken cancellationToken = default) => cleanupGroup.CreateAsync(
        $"delete owned namespace '{name}'",
        token => CreateAsync(client, name, identity, token),
        (owned, token) => DeleteAsync(client, owned, token),
        cancellationToken);

    public static async Task DeleteAsync(
        IKubernetes client,
        OwnedNamespace owned,
        CancellationToken cancellationToken = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromMinutes(1));
        V1Namespace existing;
        try
        {
            existing = await client.CoreV1.ReadNamespaceAsync(owned.Name, cancellationToken: timeout.Token);
        }
        catch (HttpOperationException exception) when (exception.Response?.StatusCode == HttpStatusCode.NotFound)
        {
            return;
        }

        if (!string.Equals(existing.Metadata.Uid, owned.Uid, StringComparison.Ordinal) ||
            existing.Metadata.Labels is null ||
            !existing.Metadata.Labels.TryGetValue(KubernetesIntegrationTestBase.NamespaceOwnerLabel, out var owner) ||
            !string.Equals(owner, KubernetesIntegrationTestBase.NamespaceOwner, StringComparison.Ordinal) ||
            !existing.Metadata.Labels.TryGetValue(
                KubernetesIntegrationTestBase.NamespaceWorktreeLabel,
                out var worktreeId) ||
            !string.Equals(worktreeId, owned.Identity.WorktreeId, StringComparison.Ordinal) ||
            !existing.Metadata.Labels.TryGetValue(KubernetesIntegrationTestBase.NamespaceRunLabel, out var runId) ||
            !string.Equals(runId, owned.Identity.RunId, StringComparison.Ordinal))
            throw new InvalidOperationException(
                $"Refusing to delete namespace '{owned.Name}' because its UID or ownership labels changed.");

        await client.CoreV1.DeleteNamespaceAsync(owned.Name, cancellationToken: timeout.Token);
        try
        {
            while (true)
            {
                try
                {
                    await client.CoreV1.ReadNamespaceAsync(owned.Name, cancellationToken: timeout.Token);
                }
                catch (HttpOperationException exception) when
                    (exception.Response?.StatusCode == HttpStatusCode.NotFound)
                {
                    return;
                }

                await Task.Delay(TimeSpan.FromSeconds(1), timeout.Token);
            }
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested &&
                                                 !cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException($"Namespace '{owned.Name}' was not deleted within one minute.");
        }
    }
}
