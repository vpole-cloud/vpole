namespace Pole.Features.ResourceConnections.Application.Kubernetes;

public static class ResourceConnectionPolicyOutcomeCodes
{
    public static string? FailureCode(this ResourceConnectionPolicyApplyOutcome outcome) => outcome switch
    {
        ResourceConnectionPolicySucceeded => null,
        ResourceConnectionPolicyDesiredStateChanged => null,
        ResourceConnectionPolicyTargetUnavailable => "TargetUnavailable",
        ResourceConnectionPolicyBaselineUnavailable => "BaselineUnavailable",
        ResourceConnectionPolicyNamespaceUnavailable => "NamespaceUnavailable",
        ResourceConnectionPolicyOwnershipConflict => "OwnershipConflict",
        ResourceConnectionPolicyConcurrentWrite => "ConcurrentWrite",
        ResourceConnectionPolicyRequestRejected => "RequestRejected",
        ResourceConnectionPolicyTransportUnavailable => "TransportUnavailable",
    };

    public static string? FailureCode(this ResourceConnectionPolicyDeleteOutcome outcome) => outcome switch
    {
        ResourceConnectionPolicySucceeded => null,
        ResourceConnectionPolicyDesiredStateChanged => null,
        ResourceConnectionPolicyTargetUnavailable => "TargetUnavailable",
        ResourceConnectionPolicyOwnershipConflict => "OwnershipConflict",
        ResourceConnectionPolicyConcurrentWrite => "ConcurrentWrite",
        ResourceConnectionPolicyRequestRejected => "RequestRejected",
        ResourceConnectionPolicyTransportUnavailable => "TransportUnavailable",
    };
}
