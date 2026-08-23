using k8s;
using k8s.Autorest;
using k8s.Models;
using Pole.Features.DeploymentTargets.Domain;
using System.Text.Json;

namespace Pole.Features.DeploymentTargets.Application.Validation;

public sealed record DeploymentTargetProbeObservation(
    string? ClusterIdentity,
    DeploymentTargetHealth Health,
    DeploymentTargetCapabilities Capabilities)
{
    public bool IsUsable =>
        ClusterIdentity is not null &&
        Health.Status == DeploymentTargetHealthStatus.Healthy;
}

public delegate Task<DeploymentTargetProbeObservation> ProbeDeploymentTargetConnection(
    IKubernetes kubernetes,
    long connectionVersion,
    DateTime? lastHealthyAtUtc = null,
    CancellationToken cancellationToken = default);

public sealed class DeploymentTargetProbe(
    TimeProvider timeProvider,
    Common.DeploymentTargetOptions options)
{
    private const int MaximumClusterIdentityLength = 512;
    private const int MaximumKubernetesVersionLength = 128;
    private static readonly string[] NamespaceVerbs = ["get", "create", "update", "patch", "delete"];

    public async Task<DeploymentTargetProbeObservation> InvokeAsync(
        IKubernetes kubernetes,
        long connectionVersion,
        DateTime? lastHealthyAtUtc = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(kubernetes);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(options.ValidationTimeoutSeconds));

        try
        {
            var version = await kubernetes.Version.GetCodeAsync(timeout.Token);
            var systemNamespace = await kubernetes.CoreV1.ReadNamespaceAsync(
                "kube-system",
                cancellationToken: timeout.Token);
            var namespaceUid = systemNamespace?.Metadata?.Uid;
            var gitVersion = version?.GitVersion;
            if (string.IsNullOrWhiteSpace(namespaceUid) || string.IsNullOrWhiteSpace(gitVersion))
            {
                return Failed(
                    connectionVersion,
                    lastHealthyAtUtc,
                    "InvalidServerIdentity",
                    "The Kubernetes API did not return a stable cluster identity.");
            }

            var normalizedNamespaceUid = namespaceUid.Trim();
            var normalizedKubernetesVersion = gitVersion.Trim();
            var identity = $"kubernetes:kube-system:{normalizedNamespaceUid}";
            if (identity.Length > MaximumClusterIdentityLength ||
                normalizedKubernetesVersion.Length > MaximumKubernetesVersionLength)
            {
                return Failed(
                    connectionVersion,
                    lastHealthyAtUtc,
                    "InvalidServerIdentity",
                    "The Kubernetes API returned an unsupported cluster identity.");
            }

            var permissions = new Dictionary<string, bool>(StringComparer.Ordinal);
            foreach (var verb in NamespaceVerbs)
            {
                var review = new V1SelfSubjectAccessReview
                {
                    ApiVersion = "authorization.k8s.io/v1",
                    Kind = "SelfSubjectAccessReview",
                    Spec = new V1SelfSubjectAccessReviewSpec
                    {
                        ResourceAttributes = new V1ResourceAttributes
                        {
                            Group = string.Empty,
                            Resource = "namespaces",
                            Verb = verb
                        }
                    }
                };
                var response = await kubernetes.AuthorizationV1.CreateSelfSubjectAccessReviewAsync(
                    review,
                    cancellationToken: timeout.Token);
                if (response is null)
                    throw new InvalidDataException("The Kubernetes API returned an empty access review.");
                permissions[verb] = response.Status?.Allowed == true;
            }

            var checkedAtUtc = GetCheckedAtUtc(lastHealthyAtUtc);
            var namespaceCapabilities = new DeploymentTargetNamespaceCapabilities(
                permissions["get"],
                permissions["create"],
                permissions["update"],
                permissions["patch"],
                permissions["delete"]);
            var capabilities = DeploymentTargetCapabilities.Observed(
                connectionVersion,
                checkedAtUtc,
                normalizedKubernetesVersion,
                namespaceCapabilities);
            if (permissions.Values.Any(allowed => !allowed))
            {
                return new DeploymentTargetProbeObservation(
                    identity,
                    DeploymentTargetHealth.Degraded(
                        connectionVersion,
                        checkedAtUtc,
                        lastHealthyAtUtc,
                        "InsufficientNamespacePermissions",
                        "The Kubernetes identity cannot manage namespaces required by Pole."),
                    capabilities);
            }

            return new DeploymentTargetProbeObservation(
                identity,
                DeploymentTargetHealth.Healthy(connectionVersion, checkedAtUtc),
                capabilities);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return Failed(
                connectionVersion,
                lastHealthyAtUtc,
                "ConnectionTimeout",
                "The Kubernetes API validation timed out.");
        }
        catch (HttpOperationException)
        {
            return Failed(
                connectionVersion,
                lastHealthyAtUtc,
                "KubernetesApiUnavailable",
                "The Kubernetes API rejected or could not complete validation.");
        }
        catch (Exception exception) when (
            exception is HttpRequestException or IOException or InvalidOperationException or
                JsonException or ArgumentException)
        {
            return Failed(
                connectionVersion,
                lastHealthyAtUtc,
                "ConnectionUnavailable",
                "The Kubernetes API connection could not be validated.");
        }
    }

    private DeploymentTargetProbeObservation Failed(
        long connectionVersion,
        DateTime? lastHealthyAtUtc,
        string code,
        string safeMessage)
    {
        var checkedAtUtc = GetCheckedAtUtc(lastHealthyAtUtc);
        return new DeploymentTargetProbeObservation(
            null,
            DeploymentTargetHealth.Degraded(
                connectionVersion,
                checkedAtUtc,
                lastHealthyAtUtc,
                code,
                safeMessage),
            DeploymentTargetCapabilities.Unknown(connectionVersion));
    }

    private DateTime GetCheckedAtUtc(DateTime? lastHealthyAtUtc)
    {
        var now = timeProvider.GetUtcNow().UtcDateTime;
        return lastHealthyAtUtc is { } lastHealthy && lastHealthy > now
            ? lastHealthy
            : now;
    }
}
