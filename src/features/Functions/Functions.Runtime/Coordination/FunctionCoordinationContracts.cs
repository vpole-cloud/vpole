using System.ComponentModel.DataAnnotations;
using System.Text;
using Microsoft.Extensions.Options;

namespace Pole.Features.Functions.Runtime.Coordination;

public static class FunctionCoordinationStorage
{
    public const string ProviderName = "function-coordination";
    public const string RevisionStateName = "function-revision-coordination";
}

public sealed class FunctionCoordinationOptions
{
    public const string SectionName = "Functions:Coordination";

    [Range(1, 30)] public int CallTimeoutSeconds { get; set; } = 5;

    [Range(5, 60)] public int InitialAdmissionLeaseSeconds { get; set; } = 15;

    [Range(150, 3600)] public int AdmissionLeaseSeconds { get; set; } = 300;

    [Range(30, 3600)] public int RecoveryGraceSeconds { get; set; } = 300;

    [Range(130, 900)] public int ActivationLeaseSeconds { get; set; } = 150;

    [Range(10, 3600)] public int ScaleTransitionLeaseSeconds { get; set; } = 300;

    [Range(1, 60)] public int ScalerOwnershipLeaseSeconds { get; set; } = 10;
}

public sealed class FunctionCoordinationOptionsValidator : IValidateOptions<FunctionCoordinationOptions>
{
    public ValidateOptionsResult Validate(string? name, FunctionCoordinationOptions options)
    {
        var failures = FunctionOptionsValidation.GetDataAnnotationFailures(options);
        if (options.AdmissionLeaseSeconds <= options.ActivationLeaseSeconds)
            failures.Add(
                $"{nameof(options.AdmissionLeaseSeconds)} must be greater than " +
                $"{nameof(options.ActivationLeaseSeconds)}.");
        if (options.InitialAdmissionLeaseSeconds <= options.CallTimeoutSeconds + 2)
            failures.Add(
                $"{nameof(options.InitialAdmissionLeaseSeconds)} must be more than two seconds greater than " +
                $"{nameof(options.CallTimeoutSeconds)}.");
        if (options.InitialAdmissionLeaseSeconds >= options.AdmissionLeaseSeconds)
            failures.Add(
                $"{nameof(options.InitialAdmissionLeaseSeconds)} must be lower than " +
                $"{nameof(options.AdmissionLeaseSeconds)}.");
        if (options.RecoveryGraceSeconds < options.AdmissionLeaseSeconds)
            failures.Add(
                $"{nameof(options.RecoveryGraceSeconds)} cannot be lower than " +
                $"{nameof(options.AdmissionLeaseSeconds)}.");

        return FunctionOptionsValidation.ToValidateOptionsResult(failures);
    }
}

[GenerateSerializer]
public sealed record FunctionAppCoordinationKey(
    [property: Id(0)] string TenantId,
    [property: Id(1)] string FunctionAppId
)
{
    public string ToGrainKey() => FunctionCoordinationKeyCodec.Encode("fa1", TenantId, FunctionAppId);

    public static bool TryParse(string value, out FunctionAppCoordinationKey? key)
    {
        if (FunctionCoordinationKeyCodec.TryDecode(value, "fa1", 2, out var components))
        {
            key = new FunctionAppCoordinationKey(components[0], components[1]);
            return true;
        }

        key = null;
        return false;
    }
}

[GenerateSerializer]
public sealed record FunctionRevisionCoordinationKey(
    [property: Id(0)] string TenantId,
    [property: Id(1)] string FunctionAppId,
    [property: Id(2)] string RevisionId
)
{
    public string ToGrainKey() => FunctionCoordinationKeyCodec.Encode("fr1", TenantId, FunctionAppId, RevisionId);

    public static bool TryParse(string value, out FunctionRevisionCoordinationKey? key)
    {
        if (FunctionCoordinationKeyCodec.TryDecode(value, "fr1", 3, out var components))
        {
            key = new FunctionRevisionCoordinationKey(components[0], components[1], components[2]);
            return true;
        }

        key = null;
        return false;
    }
}

[GenerateSerializer]
public enum FunctionAdmissionTriggerKind
{
    Http,
    Timer,
}

[GenerateSerializer]
public sealed record FunctionAdmissionRequest(
    [property: Id(0)] string AdmissionId,
    [property: Id(1)] string InvocationId,
    [property: Id(2)] string HandlerName,
    [property: Id(3)] FunctionAdmissionTriggerKind TriggerKind,
    [property: Id(4)] long ReservedBytes
);

[GenerateSerializer]
public sealed record FunctionAdmissionToken(
    [property: Id(0)] string AdmissionId,
    [property: Id(1)] string LeaseId,
    [property: Id(2)] string CoordinatorIncarnation,
    [property: Id(3)] DateTimeOffset ExpiresAtUtc
);

[GenerateSerializer]
public enum FunctionAdmissionDecisionKind
{
    Granted,
    Rejected,
}

[GenerateSerializer]
public enum FunctionAdmissionRejection
{
    None,
    InvalidRequest,
    RequestLimitExceeded,
    ByteLimitExceeded,
    IdempotencyConflict,
    RevisionQuiescing,
}

[GenerateSerializer]
public sealed record FunctionAdmissionDecision(
    [property: Id(0)] FunctionAdmissionDecisionKind Kind,
    [property: Id(1)] FunctionAdmissionToken? Token,
    [property: Id(2)] FunctionAdmissionRejection Rejection,
    [property: Id(3)] int RetryAfterMilliseconds,
    [property: Id(4)] bool RequiresActivationFence
);

[GenerateSerializer]
public sealed record FunctionAdmissionRenewalRequest(
    [property: Id(0)] FunctionAdmissionToken Token
);

[GenerateSerializer]
public enum FunctionAdmissionRenewalDecisionKind
{
    Renewed,
    Expired,
    Stale,
    InvalidRequest,
}

[GenerateSerializer]
public sealed record FunctionAdmissionRenewalDecision(
    [property: Id(0)] FunctionAdmissionRenewalDecisionKind Kind,
    [property: Id(1)] FunctionAdmissionToken? Token
);

[GenerateSerializer]
public sealed record FunctionAdmissionReleaseRequest(
    [property: Id(0)] string AdmissionId,
    [property: Id(1)] string LeaseId,
    [property: Id(2)] string CoordinatorIncarnation
);

[GenerateSerializer]
public enum FunctionAdmissionReleaseDecision
{
    Released,
    AlreadyReleasedOrExpired,
    Stale,
    InvalidRequest,
}

[GenerateSerializer]
public sealed record FunctionTrafficRequest(
    [property: Id(0)] string RequestId,
    [property: Id(1)] string HandlerName,
    [property: Id(2)] int Cost = 1
);

[GenerateSerializer]
public enum FunctionTrafficDecisionKind
{
    Granted,
    Rejected,
}

[GenerateSerializer]
public enum FunctionTrafficRejection
{
    None,
    InvalidRequest,
    RateLimitExceeded,
    IdempotencyConflict,
}

[GenerateSerializer]
public sealed record FunctionTrafficDecision(
    [property: Id(0)] FunctionTrafficDecisionKind Kind,
    [property: Id(1)] FunctionTrafficRejection Rejection,
    [property: Id(2)] int RetryAfterMilliseconds
);

[GenerateSerializer]
public sealed record FunctionTrafficSnapshot(
    [property: Id(0)] FunctionAppCoordinationKey Key,
    [property: Id(1)] double AvailableTokens,
    [property: Id(2)] int RequestsPerSecond,
    [property: Id(3)] int Burst
);

[GenerateSerializer]
public sealed record FunctionActivationBeginRequest(
    [property: Id(0)] string OperationId,
    [property: Id(1)] int DesiredReplicas
);

[GenerateSerializer]
public sealed record FunctionActivationLease(
    [property: Id(0)] string OperationId,
    [property: Id(1)] string LeaseId,
    [property: Id(2)] long Generation,
    [property: Id(3)] int DesiredReplicas,
    [property: Id(4)] DateTimeOffset ExpiresAtUtc
);

[GenerateSerializer]
public enum FunctionActivationBeginDecisionKind
{
    Granted,
    AlreadyDesired,
    Busy,
    Rejected,
}

[GenerateSerializer]
public enum FunctionActivationRejection
{
    None,
    InvalidRequest,
    ScaleTransitionInProgress,
}

[GenerateSerializer]
public sealed record FunctionActivationBeginDecision(
    [property: Id(0)] FunctionActivationBeginDecisionKind Kind,
    [property: Id(1)] FunctionActivationLease? Lease,
    [property: Id(2)] FunctionActivationRejection Rejection,
    [property: Id(3)] int RetryAfterMilliseconds
);

[GenerateSerializer]
public enum FunctionCoordinationCompletionOutcome
{
    Applied,
    Failed,
}

[GenerateSerializer]
public sealed record FunctionActivationCompleteRequest(
    [property: Id(0)] string LeaseId,
    [property: Id(1)] long Generation,
    [property: Id(2)] FunctionCoordinationCompletionOutcome Outcome
);

[GenerateSerializer]
public enum FunctionActivationCompleteDecision
{
    Completed,
    AlreadyCompleted,
    Stale,
    InvalidRequest,
}

[GenerateSerializer]
public sealed record FunctionScalerOwnershipRequest(
    [property: Id(0)] string OperationId,
    [property: Id(1)] string OwnerId
);

[GenerateSerializer]
public sealed record FunctionScalerOwnershipToken(
    [property: Id(0)] string OperationId,
    [property: Id(1)] string OwnerId,
    [property: Id(2)] string LeaseId,
    [property: Id(3)] string CoordinatorIncarnation,
    [property: Id(4)] DateTimeOffset ExpiresAtUtc
);

[GenerateSerializer]
public enum FunctionScalerOwnershipDecisionKind
{
    Granted,
    Busy,
    Rejected,
}

[GenerateSerializer]
public sealed record FunctionScalerOwnershipDecision(
    [property: Id(0)] FunctionScalerOwnershipDecisionKind Kind,
    [property: Id(1)] FunctionScalerOwnershipToken? Token,
    [property: Id(2)] int RetryAfterMilliseconds
);

[GenerateSerializer]
public enum FunctionScalerOwnershipReleaseDecision
{
    Released,
    AlreadyReleasedOrExpired,
    Stale,
    InvalidRequest,
}

[GenerateSerializer]
public sealed record FunctionQuiescingBeginRequest(
    [property: Id(0)] string OperationId,
    [property: Id(1)] FunctionScalerOwnershipToken ScalerToken
);

[GenerateSerializer]
public sealed record FunctionQuiescingLease(
    [property: Id(0)] string OperationId,
    [property: Id(1)] string LeaseId,
    [property: Id(2)] long Epoch,
    [property: Id(3)] DateTimeOffset ExpiresAtUtc
);

[GenerateSerializer]
public enum FunctionQuiescingDecisionKind
{
    Granted,
    Busy,
    RecoveryGrace,
    ActiveAdmissions,
    Stale,
    Rejected,
}

[GenerateSerializer]
public sealed record FunctionQuiescingDecision(
    [property: Id(0)] FunctionQuiescingDecisionKind Kind,
    [property: Id(1)] FunctionQuiescingLease? Lease,
    [property: Id(2)] int RetryAfterMilliseconds
);

[GenerateSerializer]
public enum FunctionQuiescingCompletionOutcome
{
    Converged,
    Aborted,
}

[GenerateSerializer]
public sealed record FunctionQuiescingCompleteRequest(
    [property: Id(0)] FunctionScalerOwnershipToken ScalerToken,
    [property: Id(1)] string LeaseId,
    [property: Id(2)] long Epoch,
    [property: Id(3)] FunctionQuiescingCompletionOutcome Outcome
);

[GenerateSerializer]
public enum FunctionQuiescingCompleteDecision
{
    Completed,
    Stale,
    Busy,
    ActiveAdmissions,
    InvalidRequest,
}

[GenerateSerializer]
public sealed record FunctionScaleBeginRequest(
    [property: Id(0)] string OperationId,
    [property: Id(1)] FunctionScalerOwnershipToken ScalerToken,
    [property: Id(2)] int DesiredReplicas,
    [property: Id(3)] long? QuiescingEpoch,
    [property: Id(4)] int ObservedReplicas
);

[GenerateSerializer]
public sealed record FunctionScaleLease(
    [property: Id(0)] string OperationId,
    [property: Id(1)] string IntentId,
    [property: Id(2)] long Generation,
    [property: Id(3)] int DesiredReplicas,
    [property: Id(4)] DateTimeOffset ExpiresAtUtc
);

[GenerateSerializer]
public enum FunctionScaleBeginDecisionKind
{
    Granted,
    AlreadyDesired,
    Busy,
    RecoveryGrace,
    QuiescingRequired,
    ActiveAdmissions,
    Stale,
    Rejected,
}

[GenerateSerializer]
public sealed record FunctionScaleBeginDecision(
    [property: Id(0)] FunctionScaleBeginDecisionKind Kind,
    [property: Id(1)] FunctionScaleLease? Lease,
    [property: Id(2)] int RetryAfterMilliseconds
);

[GenerateSerializer]
public sealed record FunctionScaleCompleteRequest(
    [property: Id(0)] string IntentId,
    [property: Id(1)] long Generation,
    [property: Id(2)] FunctionCoordinationCompletionOutcome Outcome,
    [property: Id(3)] FunctionScalerOwnershipToken ScalerToken
);

[GenerateSerializer]
public enum FunctionScaleCompleteDecision
{
    Completed,
    AlreadyCompleted,
    Stale,
    InvalidRequest,
}

[GenerateSerializer]
public sealed record FunctionAdmissionSnapshot(
    [property: Id(0)] int Requests,
    [property: Id(1)] long Bytes,
    [property: Id(2)] Dictionary<string, int> HandlerRequests
);

[GenerateSerializer]
public sealed record FunctionRevisionCoordinatorSnapshot(
    [property: Id(0)] FunctionRevisionCoordinationKey Key,
    [property: Id(1)] string CoordinatorIncarnation,
    [property: Id(2)] long Generation,
    [property: Id(3)] int? DesiredReplicas,
    [property: Id(4)] DateTimeOffset ScaleDownBlockedUntilUtc,
    [property: Id(5)] FunctionAdmissionSnapshot Admissions,
    [property: Id(6)] FunctionActivationIntent? ActivationIntent,
    [property: Id(7)] FunctionQuiescingIntent? QuiescingIntent,
    [property: Id(8)] FunctionScaleIntent? PendingScaleIntent,
    [property: Id(9)] FunctionScalerOwnershipToken? ScalerOwnership
);

[GenerateSerializer]
public sealed record FunctionActivationIntent(
    [property: Id(0)] string OperationId,
    [property: Id(1)] string LeaseId,
    [property: Id(2)] long Generation,
    [property: Id(3)] int DesiredReplicas,
    [property: Id(4)] int? PreviousDesiredReplicas,
    [property: Id(5)] DateTimeOffset StartedAtUtc,
    [property: Id(6)] DateTimeOffset ExpiresAtUtc
);

[GenerateSerializer]
public sealed record FunctionActivationCompletion(
    [property: Id(0)] string LeaseId,
    [property: Id(1)] long Generation,
    [property: Id(2)] FunctionCoordinationCompletionOutcome Outcome
);

[GenerateSerializer]
public sealed record FunctionQuiescingIntent(
    [property: Id(0)] string OperationId,
    [property: Id(1)] string LeaseId,
    [property: Id(2)] long Epoch,
    [property: Id(3)] DateTimeOffset StartedAtUtc,
    [property: Id(4)] DateTimeOffset ExpiresAtUtc
);

[GenerateSerializer]
public sealed record FunctionScaleIntent(
    [property: Id(0)] string OperationId,
    [property: Id(1)] string IntentId,
    [property: Id(2)] long Generation,
    [property: Id(3)] int DesiredReplicas,
    [property: Id(4)] int? PreviousDesiredReplicas,
    [property: Id(5)] long? QuiescingEpoch,
    [property: Id(6)] DateTimeOffset StartedAtUtc,
    [property: Id(7)] DateTimeOffset ExpiresAtUtc
);

[GenerateSerializer]
public sealed record FunctionScaleCompletion(
    [property: Id(0)] string IntentId,
    [property: Id(1)] long Generation,
    [property: Id(2)] FunctionCoordinationCompletionOutcome Outcome
);

[GenerateSerializer]
public sealed class FunctionRevisionCoordinatorState
{
    public const int CurrentSchemaVersion = 2;

    [Id(0)] public int SchemaVersion { get; set; } = CurrentSchemaVersion;

    [Id(1)] public long Generation { get; set; }

    [Id(2)] public int? DesiredReplicas { get; set; }

    [Id(3)] public FunctionActivationIntent? ActivationIntent { get; set; }

    [Id(4)] public FunctionActivationCompletion? LastActivationCompletion { get; set; }

    [Id(5)] public FunctionQuiescingIntent? QuiescingIntent { get; set; }

    [Id(6)] public FunctionScaleIntent? PendingScaleIntent { get; set; }

    [Id(7)] public FunctionScaleCompletion? LastScaleCompletion { get; set; }

    [Id(8)] public bool AdmissionFenceRequired { get; set; }

    public FunctionRevisionCoordinatorState Copy() => new()
    {
        SchemaVersion = SchemaVersion,
        Generation = Generation,
        DesiredReplicas = DesiredReplicas,
        ActivationIntent = ActivationIntent,
        LastActivationCompletion = LastActivationCompletion,
        QuiescingIntent = QuiescingIntent,
        PendingScaleIntent = PendingScaleIntent,
        LastScaleCompletion = LastScaleCompletion,
        AdmissionFenceRequired = AdmissionFenceRequired,
    };
}

public interface IFunctionRevisionCoordinatorGrain : IGrainWithStringKey
{
    Task<FunctionAdmissionDecision> AcquireAdmissionAsync(FunctionAdmissionRequest request);
    Task<FunctionAdmissionRenewalDecision> RenewAdmissionAsync(FunctionAdmissionRenewalRequest request);
    Task<FunctionAdmissionReleaseDecision> ReleaseAdmissionAsync(FunctionAdmissionReleaseRequest request);
    Task<FunctionActivationBeginDecision> BeginActivationAsync(FunctionActivationBeginRequest request);
    Task<FunctionActivationCompleteDecision> CompleteActivationAsync(FunctionActivationCompleteRequest request);
    Task<FunctionScalerOwnershipDecision> TryAcquireScalerOwnershipAsync(FunctionScalerOwnershipRequest request);
    Task<FunctionScalerOwnershipDecision> RenewScalerOwnershipAsync(FunctionScalerOwnershipToken token);
    Task<FunctionScalerOwnershipReleaseDecision> ReleaseScalerOwnershipAsync(FunctionScalerOwnershipToken token);
    Task<FunctionQuiescingDecision> BeginQuiescingAsync(FunctionQuiescingBeginRequest request);
    Task<FunctionQuiescingCompleteDecision> CompleteQuiescingAsync(FunctionQuiescingCompleteRequest request);
    Task<FunctionScaleBeginDecision> BeginScaleAsync(FunctionScaleBeginRequest request);
    Task<FunctionScaleCompleteDecision> CompleteScaleAsync(FunctionScaleCompleteRequest request);
    Task<FunctionRevisionCoordinatorSnapshot> GetSnapshotAsync();
}

public interface IFunctionAppTrafficGrain : IGrainWithStringKey
{
    Task<FunctionTrafficDecision> TryAcquireAsync(FunctionTrafficRequest request);
    Task<FunctionTrafficSnapshot> GetSnapshotAsync();
}

internal static class FunctionCoordinationKeyCodec
{
    private static readonly UTF8Encoding Utf8 = new(false, true);

    public static string Encode(string prefix, params string[] components)
    {
        if (string.IsNullOrWhiteSpace(prefix) || components.Any(string.IsNullOrWhiteSpace))
            throw new ArgumentException("Function coordination key components must not be blank.");

        return string.Join('.', [prefix, .. components.Select(EncodeComponent)]);
    }

    public static bool TryDecode(
        string value,
        string expectedPrefix,
        int componentCount,
        out string[] components)
    {
        components = [];
        if (string.IsNullOrWhiteSpace(value))
            return false;
        var parts = value.Split('.');
        if (parts.Length != componentCount + 1 || !string.Equals(parts[0], expectedPrefix, StringComparison.Ordinal))
            return false;

        try
        {
            components = parts.Skip(1).Select(DecodeComponent).ToArray();
            return components.All(component => !string.IsNullOrWhiteSpace(component));
        }
        catch (Exception exception) when (exception is FormatException or DecoderFallbackException)
        {
            components = [];
            return false;
        }
    }

    private static string EncodeComponent(string value) => Convert.ToBase64String(Utf8.GetBytes(value))
        .TrimEnd('=')
        .Replace('+', '-')
        .Replace('/', '_');

    private static string DecodeComponent(string value)
    {
        var base64 = value.Replace('-', '+').Replace('_', '/');
        base64 = (base64.Length % 4) switch
        {
            0 => base64,
            2 => base64 + "==",
            3 => base64 + "=",
            _ => throw new FormatException("Invalid Base64Url component."),
        };
        return Utf8.GetString(Convert.FromBase64String(base64));
    }
}
