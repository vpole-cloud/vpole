namespace Pole.Features.Secrets.Application.Projections;

public sealed record SecretProjectionTargetUnavailable;
public sealed record SecretProjectionTargetDisabled;
public sealed record SecretProjectionOwnershipConflict;
public sealed record SecretProjectionDesiredStateChanged;
public sealed record SecretProjectionNamespaceUnavailable;
public sealed record SecretProjectionConcurrentWrite;
public sealed record SecretProjectionRequestRejected;
public sealed record SecretProjectionTransportUnavailable;
public sealed record SecretProjectionDecryptionUnavailable;

public union SecretProjectionTargetError(
    SecretProjectionTargetUnavailable,
    SecretProjectionTargetDisabled,
    SecretProjectionOwnershipConflict,
    SecretProjectionDesiredStateChanged,
    SecretProjectionNamespaceUnavailable,
    SecretProjectionConcurrentWrite,
    SecretProjectionRequestRejected,
    SecretProjectionTransportUnavailable,
    SecretProjectionDecryptionUnavailable);

internal static class SecretProjectionTargetErrorCodes
{
    public static string Code(SecretProjectionTargetError error) => error switch
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
