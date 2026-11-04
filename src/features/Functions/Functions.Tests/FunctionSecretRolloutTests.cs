using k8s.Models;
using Pole.Features.Functions.Runtime.Kubernetes;
using Pole.Features.Core.Kubernetes;
using Xunit;

namespace Pole.Features.Functions.Tests;

public sealed class FunctionSecretRolloutTests
{
    private static readonly WorkloadIdentityLabels Identity = new(
        "tenant", "group", "scope", "cell", "target", "function-app", "app", "deployment");

    [Theory]
    [InlineData("current", true)]
    [InlineData("old-revision", false)]
    [InlineData("terminating", false)]
    [InlineData("truncated", false)]
    [InlineData("unobserved", false)]
    [InlineData("missing-pods", false)]
    [InlineData("wrong-owner", false)]
    public void Retirement_requires_complete_current_native_evidence(string scenario, bool expected)
    {
        var labels = new Dictionary<string, string>(Identity.ToDictionary())
        {
            ["pole.io/function-revision-id"] = "current"
        };
        var deployment = new V1Deployment
        {
            Metadata = new V1ObjectMeta
            {
                Name = "functions-r2",
                Uid = "deployment-uid",
                NamespaceProperty = "runtime",
                Generation = 2,
                Labels = labels
            },
            Spec = new V1DeploymentSpec { Replicas = 1 },
            Status = new V1DeploymentStatus
            {
                ObservedGeneration = 2,
                UpdatedReplicas = 1,
                ReadyReplicas = 1,
                AvailableReplicas = 1
            }
        };
        var pod = new V1Pod
        {
            Metadata = new V1ObjectMeta
            {
                Name = "current-pod",
                Uid = "pod-uid",
                NamespaceProperty = "runtime",
                Labels = new Dictionary<string, string>(labels)
            },
            Status = new V1PodStatus
            {
                Conditions = [new V1PodCondition { Type = "Ready", Status = "True" }]
            }
        };
        var pods = new V1PodList { Metadata = new V1ListMeta(), Items = [pod] };
        switch (scenario)
        {
            case "old-revision": pod.Metadata.Labels["pole.io/function-revision-id"] = "previous"; break;
            case "terminating": pod.Metadata.DeletionTimestamp = DateTime.UtcNow; break;
            case "truncated": pods.Metadata.ContinueProperty = "next"; break;
            case "unobserved": deployment.Status.ObservedGeneration = 1; break;
            case "missing-pods": pods.Items.Clear(); break;
            case "wrong-owner": pod.Metadata.Labels["pole.io/tenant-id"] = "other"; break;
        }

        Assert.Equal(expected, FunctionSecretRolloutObserver.ConfirmsRetirement(
            deployment, pods, "current", "runtime", Identity));
    }
}
