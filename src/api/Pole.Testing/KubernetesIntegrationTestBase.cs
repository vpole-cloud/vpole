using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using k8s;
using k8s.Autorest;
using k8s.Models;
using Xunit;

namespace Pole.Testing;

[Collection(CollectionName)]
public abstract class KubernetesIntegrationTestBase(
    KubernetesClusterFixture fixture) : IAsyncLifetime
{
    public const string CollectionName = "kubernetes-integration-tests";
    public const string NamespaceOwnerLabel = TestRunIdentity.OwnerLabel;
    public const string NamespaceWorktreeLabel = TestRunIdentity.WorktreeLabel;
    public const string NamespaceRunLabel = TestRunIdentity.RunLabel;
    public const string NamespaceOwner = TestRunIdentity.Owner;

    private string? _namespaceUid;
    private string? _runId;

    protected IKubernetes Kubernetes => fixture.Client;
    protected KubernetesLabTarget Target => fixture.Target;
    protected string NamespaceName { get; private set; } = null!;

    protected async Task<TimeoutException> CreateTimeoutExceptionAsync(
        string message,
        Exception? innerException = null)
    {
        var diagnostics = new StringBuilder();
        diagnostics.AppendLine($"Target: {Target.Key}; namespace: {NamespaceName}");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try
        {
            var namespaceResource = await Kubernetes.CoreV1.ReadNamespaceAsync(
                NamespaceName,
                cancellationToken: timeout.Token);
            diagnostics.AppendLine($"Namespace phase: {namespaceResource.Status?.Phase ?? "unknown"}");

            var pods = await Kubernetes.CoreV1.ListNamespacedPodAsync(
                NamespaceName,
                cancellationToken: timeout.Token);
            foreach (var pod in pods.Items.Take(12))
            {
                var containers = pod.Status?.ContainerStatuses ?? [];
                diagnostics.AppendLine(
                    $"Pod {pod.Metadata.Name}: phase={pod.Status?.Phase ?? "unknown"}; " +
                    $"node={pod.Spec?.NodeName ?? "unassigned"}; " +
                    $"containers={string.Join(",", containers.Select(status =>
                        $"{status.Name}:ready={status.Ready}:restarts={status.RestartCount}:" +
                        $"waiting={status.State?.Waiting?.Reason ?? "-"}:terminated={status.State?.Terminated?.Reason ?? "-"}"))}");

            }

            var deployments = await Kubernetes.AppsV1.ListNamespacedDeploymentAsync(
                NamespaceName,
                cancellationToken: timeout.Token);
            foreach (var deployment in deployments.Items.Take(12))
            {
                diagnostics.AppendLine(
                    $"Deployment {deployment.Metadata.Name}: desired={deployment.Spec.Replicas}; " +
                    $"current={deployment.Status.Replicas}; ready={deployment.Status.ReadyReplicas}; " +
                    $"available={deployment.Status.AvailableReplicas}");
            }

            var statefulSets = await Kubernetes.AppsV1.ListNamespacedStatefulSetAsync(
                NamespaceName,
                cancellationToken: timeout.Token);
            foreach (var statefulSet in statefulSets.Items.Take(12))
            {
                diagnostics.AppendLine(
                    $"StatefulSet {statefulSet.Metadata.Name}: desired={statefulSet.Spec.Replicas}; " +
                    $"current={statefulSet.Status.CurrentReplicas}; ready={statefulSet.Status.ReadyReplicas}");
            }

            var events = await Kubernetes.CoreV1.ListNamespacedEventAsync(
                NamespaceName,
                cancellationToken: timeout.Token);
            foreach (var item in events.Items.TakeLast(20))
            {
                diagnostics.AppendLine(
                    $"Event {item.Type}/{item.Reason}: {item.InvolvedObject.Kind}/{item.InvolvedObject.Name}: " +
                    item.Message);
            }
        }
        catch (Exception exception)
        {
            diagnostics.AppendLine($"Diagnostic collection failed: {exception.GetType().Name}");
        }

        var safeDiagnostics = SanitizeDiagnostic(diagnostics.ToString());
        if (safeDiagnostics.Length > 8_000)
            safeDiagnostics = safeDiagnostics[..8_000] + "<truncated>";
        return new TimeoutException($"{message}{Environment.NewLine}{safeDiagnostics}", innerException);
    }

    public static string SanitizeDiagnostic(string value)
    {
        if (string.IsNullOrEmpty(value))
            return value;

        var sanitized = Regex.Replace(
            value,
            @"(?i)\b(authorization|password|passwd|token|secret|connection[-_ ]?string)\b[\""']?\s*[:=]\s*[\""']?[^\""'\s,;}]+",
            "$1=<redacted>");
        sanitized = Regex.Replace(sanitized, @"(?i)\bbearer\s+[^\s]+", "Bearer <redacted>");
        sanitized = Regex.Replace(sanitized, @"(?i)(https?://)[^/@\s:]+:[^/@\s]+@", "$1<redacted>@");
        sanitized = Regex.Replace(sanitized, @"\beyJ[A-Za-z0-9_-]{16,}(?:\.[A-Za-z0-9_-]+){1,2}\b", "<redacted-jwt>");
        sanitized = Regex.Replace(sanitized, @"\b[A-Za-z0-9+/=_-]{48,}\b", "<redacted-value>");
        return sanitized;
    }

    public virtual async Task InitializeAsync()
    {
        _runId = fixture.Identity.RunId;
        NamespaceName = fixture.Identity.CreateNamespaceName("k8s");

        var created = await Kubernetes.CoreV1.CreateNamespaceAsync(new V1Namespace
        {
            Metadata = new V1ObjectMeta
            {
                Name = NamespaceName,
                Labels = new Dictionary<string, string>(fixture.Identity.NamespaceLabels, StringComparer.Ordinal)
            }
        });
        _namespaceUid = created.Metadata.Uid;
    }

    public virtual async Task DisposeAsync()
    {
        if (string.IsNullOrWhiteSpace(NamespaceName) ||
            string.IsNullOrWhiteSpace(_namespaceUid) ||
            string.IsNullOrWhiteSpace(_runId))
        {
            return;
        }

        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        V1Namespace existing;
        try
        {
            existing = await Kubernetes.CoreV1.ReadNamespaceAsync(
                NamespaceName,
                cancellationToken: timeout.Token);
        }
        catch (HttpOperationException exception) when (exception.Response?.StatusCode == HttpStatusCode.NotFound)
        {
            return;
        }

        if (!string.Equals(existing.Metadata.Uid, _namespaceUid, StringComparison.Ordinal) ||
            existing.Metadata.Labels is null ||
            !existing.Metadata.Labels.TryGetValue(NamespaceOwnerLabel, out var owner) ||
            !string.Equals(owner, NamespaceOwner, StringComparison.Ordinal) ||
            !existing.Metadata.Labels.TryGetValue(NamespaceWorktreeLabel, out var worktreeId) ||
            !string.Equals(worktreeId, fixture.Identity.WorktreeId, StringComparison.Ordinal) ||
            !existing.Metadata.Labels.TryGetValue(NamespaceRunLabel, out var runId) ||
            !string.Equals(runId, _runId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Refusing to delete namespace '{NamespaceName}' on target '{Target.Key}' because its UID or ownership labels changed.");
        }

        await Kubernetes.CoreV1.DeleteNamespaceAsync(NamespaceName, cancellationToken: timeout.Token);
        try
        {
            while (true)
            {
                try
                {
                    await Kubernetes.CoreV1.ReadNamespaceAsync(
                        NamespaceName,
                        cancellationToken: timeout.Token);
                }
                catch (HttpOperationException exception) when (exception.Response?.StatusCode == HttpStatusCode.NotFound)
                {
                    return;
                }

                await Task.Delay(TimeSpan.FromSeconds(1), timeout.Token);
            }
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested)
        {
            throw new TimeoutException(
                $"Namespace '{NamespaceName}' on target '{Target.Key}' was not deleted within three minutes.");
        }
    }
}
