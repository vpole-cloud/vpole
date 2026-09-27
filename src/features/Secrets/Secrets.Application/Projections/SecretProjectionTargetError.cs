namespace Pole.Features.Secrets.Application.Projections;

public abstract record SecretProjectionTargetFailure;
public sealed record SecretProjectionTargetUnavailable : SecretProjectionTargetFailure;
public sealed record SecretProjectionTargetDisabled : SecretProjectionTargetFailure;
public sealed record SecretProjectionOwnershipConflict : SecretProjectionTargetFailure;
public sealed record SecretProjectionDesiredStateChanged : SecretProjectionTargetFailure;
public sealed record SecretProjectionNamespaceUnavailable : SecretProjectionTargetFailure;
public sealed record SecretProjectionConcurrentWrite : SecretProjectionTargetFailure;
public sealed record SecretProjectionRequestRejected : SecretProjectionTargetFailure;
public sealed record SecretProjectionTransportUnavailable : SecretProjectionTargetFailure;
public sealed record SecretProjectionDecryptionUnavailable : SecretProjectionTargetFailure;

public sealed record SecretProjectionTargetSucceeded;

public union SecretProjectionTargetOutcome(
    SecretProjectionTargetSucceeded,
    SecretProjectionTargetFailure);

public union SecretProjectionManifestOutcome(
    k8s.Models.V1Secret,
    SecretProjectionTargetFailure);

internal static class SecretProjectionTargetErrorCodes
{
    public static string Code(SecretProjectionTargetFailure error) => error switch
    {
        SecretProjectionTargetUnavailable => "TargetUnavailable",
        SecretProjectionTargetDisabled => "TargetDisabled",
        SecretProjectionOwnershipConflict => "OwnershipConflict",
        SecretProjectionDesiredStateChanged => "DesiredStateChanged",
        SecretProjectionNamespaceUnavailable => "NamespaceUnavailable",
        SecretProjectionConcurrentWrite => "ConcurrentWrite",
        SecretProjectionRequestRejected => "RequestRejected",
        SecretProjectionTransportUnavailable => "TransportUnavailable",
        SecretProjectionDecryptionUnavailable => "DecryptionUnavailable"
    };
}
