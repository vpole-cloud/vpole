using System.Collections.Immutable;
using System.Text.Json.Serialization;
using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace Pole.Features.Functions.Domain;

public sealed record FunctionApp(
    [property: BsonId]
    [property: BsonRepresentation(BsonType.ObjectId)]
    string Id,
    string TenantId,
    string ResourceGroupId,
    string Name,
    string NormalizedName,
    string FunctionRoute,
    string? ActiveRevisionId,
    string? PendingRevisionId,
    int NextRevisionNumber,
    FunctionAppDesiredState DesiredState,
    FunctionAppStatus Status,
    FunctionQuotaAllocation AdmittedQuota,
    ImmutableHashSet<string> Tags,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc);

public enum FunctionAppDesiredState
{
    Enabled,
    Suspended,
    Deleting
}

public enum FunctionAppStatus
{
    Provisioning,
    Ready,
    Updating,
    Suspended,
    Failed,
    Deleting
}

public sealed record FunctionRevision(
    [property: BsonId]
    [property: BsonRepresentation(BsonType.ObjectId)]
    string Id,
    string TenantId,
    string FunctionAppId,
    int RevisionNumber,
    FunctionRevisionSpec Spec,
    FunctionRevisionState State,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset? ReadyAtUtc,
    FunctionRevisionFailure? Failure)
{
    public static FunctionRevision CreateProvisioning(
        string id,
        string tenantId,
        string functionAppId,
        int revisionNumber,
        FunctionRevisionSpec spec,
        DateTimeOffset createdAtUtc) =>
        new(
            id,
            tenantId,
            functionAppId,
            revisionNumber,
            spec,
            FunctionRevisionState.Provisioning,
            createdAtUtc,
            ReadyAtUtc: null,
            Failure: null);

    public FunctionRevision Activate(DateTimeOffset readyAtUtc)
    {
        if (State != FunctionRevisionState.Provisioning)
            throw new InvalidOperationException($"Cannot activate Function revision in state '{State}'.");
        return this with
        {
            State = FunctionRevisionState.Active,
            ReadyAtUtc = readyAtUtc,
            Failure = null
        };
    }

    public FunctionRevision Drain()
    {
        if (State != FunctionRevisionState.Active || ReadyAtUtc is null)
            throw new InvalidOperationException($"Cannot drain Function revision in state '{State}'.");
        return this with { State = FunctionRevisionState.Draining };
    }

    public FunctionRevision Retire()
    {
        if (State != FunctionRevisionState.Draining || ReadyAtUtc is null)
            throw new InvalidOperationException($"Cannot retire Function revision in state '{State}'.");
        return this with { State = FunctionRevisionState.Retired };
    }

    public FunctionRevision Fail(FunctionRevisionFailure failure)
    {
        if (State != FunctionRevisionState.Provisioning)
            throw new InvalidOperationException($"Cannot fail Function revision in state '{State}'.");
        return this with
        {
            State = FunctionRevisionState.Failed,
            ReadyAtUtc = null,
            Failure = failure
        };
    }

    public bool HasValidLifecycleState() => State switch
    {
        FunctionRevisionState.Provisioning => ReadyAtUtc is null && Failure is null,
        FunctionRevisionState.Ready or
        FunctionRevisionState.Active or
        FunctionRevisionState.Draining or
        FunctionRevisionState.Retired => ReadyAtUtc is not null && Failure is null,
        FunctionRevisionState.Failed => ReadyAtUtc is null && Failure is not null,
        _ => false
    };
}

public sealed record FunctionRevisionSpec(
    string KubernetesNamespace,
    FunctionImage Image,
    string RuntimeProtocolVersion,
    FunctionConfiguration Configuration,
    FunctionResources Resources,
    FunctionScalingPolicy Scaling,
    ImmutableList<FunctionHandlerSpec> Handlers);

public sealed record FunctionImage(
    string Server,
    string Name,
    string Tag,
    string? Digest = null,
    string? PullSecretName = null)
{
    [BsonIgnore]
    public string Reference => Digest is null
        ? $"{Server}/{Name}:{Tag}"
        : $"{Server}/{Name}@{Digest}";
}

public sealed record FunctionConfiguration(
    ImmutableDictionary<string, string> EnvironmentVariables,
    ImmutableDictionary<string, string> SecretReferences);

public sealed record FunctionResources(
    FunctionResourceValues Requests,
    FunctionResourceValues Limits);

public sealed record FunctionResourceValues(int CpuMillis, int MemoryMi);

public sealed record FunctionScalingPolicy(
    int MinInstances,
    int MaxInstances,
    int TargetConcurrencyPerInstance,
    int MaxConcurrencyPerInstance,
    int ScaleDownDelaySeconds,
    int ActivationTimeoutSeconds);

public sealed record FunctionHandlerSpec(
    string Name,
    int MaxConcurrencyPerInstance,
    FunctionTriggerSpec Trigger);

[BsonDiscriminator(RootClass = true)]
[BsonKnownTypes(typeof(HttpFunctionTriggerSpec), typeof(TimerFunctionTriggerSpec))]
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(HttpFunctionTriggerSpec), "http")]
[JsonDerivedType(typeof(TimerFunctionTriggerSpec), "timer")]
public abstract record FunctionTriggerSpec(string TriggerId);

[BsonDiscriminator("http")]
public sealed record HttpFunctionTriggerSpec(
    string TriggerId,
    string Route,
    ImmutableHashSet<string> Methods,
    FunctionExposure Exposure)
    : FunctionTriggerSpec(TriggerId);

[BsonDiscriminator("timer")]
public sealed record TimerFunctionTriggerSpec(
    string TriggerId,
    string Cron,
    string TimeZone,
    TimerConcurrencyPolicy ConcurrencyPolicy,
    TimerMisfirePolicy MisfirePolicy,
    int ScheduleVersion,
    FunctionRetryPolicy Retry)
    : FunctionTriggerSpec(TriggerId);

public enum FunctionExposure
{
    Internal,
    Public
}

public enum TimerConcurrencyPolicy
{
    Allow,
    Forbid
}

public enum TimerMisfirePolicy
{
    Skip,
    FireOnce
}

public sealed record FunctionRetryPolicy(
    int MaxAttempts,
    ImmutableList<int> BackoffSeconds);

public enum FunctionRevisionState
{
    Provisioning,
    Ready,
    Active,
    Draining,
    Failed,
    Retired
}

public sealed record FunctionRevisionFailure(string Code, string Message);

public sealed record FunctionRouteBinding(
    [property: BsonId]
    string NormalizedRoute,
    string TenantId,
    string FunctionAppId,
    ImmutableList<FunctionHttpRouteBinding> HttpRoutes,
    FunctionRouteBindingState State,
    string? ActiveRevisionId,
    int? ActiveRevisionNumber,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc);

public sealed record FunctionHttpRouteBinding(
    string TriggerId,
    string HandlerName,
    string HandlerRoute,
    ImmutableHashSet<string> Methods,
    FunctionExposure Exposure);

public enum FunctionRouteBindingState
{
    Reserved,
    Active,
    Disabled
}

public sealed record FunctionQuotaAllocation(
    long FunctionAppCount,
    long TotalCpuMillis,
    long TotalMemoryMi,
    long WorkloadInstances)
{
    public static FunctionQuotaAllocation FromRevision(FunctionRevisionSpec spec) =>
        new(
            1,
            checked((long)spec.Resources.Requests.CpuMillis * spec.Scaling.MaxInstances),
            checked((long)spec.Resources.Requests.MemoryMi * spec.Scaling.MaxInstances),
            spec.Scaling.MaxInstances);

    public static FunctionQuotaAllocation Peak(
        FunctionQuotaAllocation first,
        FunctionQuotaAllocation second) =>
        new(
            Math.Max(first.FunctionAppCount, second.FunctionAppCount),
            Math.Max(first.TotalCpuMillis, second.TotalCpuMillis),
            Math.Max(first.TotalMemoryMi, second.TotalMemoryMi),
            Math.Max(first.WorkloadInstances, second.WorkloadInstances));
}

public static class FunctionAppFactory
{
    public sealed record CreateResult(
        FunctionApp FunctionApp,
        FunctionRevision Revision,
        FunctionRouteBinding RouteBinding);

    public static CreateResult Create(
        string tenantId,
        string resourceGroupId,
        string kubernetesNamespace,
        string name,
        string functionRoute,
        FunctionRevisionSpecInput spec,
        ImmutableHashSet<string> tags,
        DateTimeOffset now)
    {
        var functionAppId = ObjectId.GenerateNewId().ToString();
        var revisionId = ObjectId.GenerateNewId().ToString();
        var normalizedRoute = NormalizeRoute(functionRoute);
        var revisionSpec = spec.ToRevisionSpec(kubernetesNamespace) with
        {
            Handlers = spec.Handlers
                .Select(handler => handler with
                {
                    Trigger = handler.Trigger switch
                    {
                        HttpFunctionTriggerSpec http => http with
                        {
                            TriggerId = Guid.NewGuid().ToString("N")
                        },
                        TimerFunctionTriggerSpec timer => timer with
                        {
                            TriggerId = Guid.NewGuid().ToString("N"),
                            ScheduleVersion = 1
                        },
                        _ => throw new InvalidOperationException("Unknown Function trigger spec.")
                    }
                })
                .ToImmutableList()
        };
        var app = new FunctionApp(
            functionAppId,
            tenantId,
            resourceGroupId,
            name,
            name.Trim().ToLowerInvariant(),
            normalizedRoute,
            ActiveRevisionId: null,
            PendingRevisionId: revisionId,
            NextRevisionNumber: 2,
            FunctionAppDesiredState.Enabled,
            FunctionAppStatus.Provisioning,
            FunctionQuotaAllocation.FromRevision(revisionSpec),
            tags,
            now,
            now);
        var revision = FunctionRevision.CreateProvisioning(
            revisionId,
            tenantId,
            functionAppId,
            revisionNumber: 1,
            revisionSpec,
            now);
        var binding = new FunctionRouteBinding(
            normalizedRoute,
            tenantId,
            functionAppId,
            [],
            FunctionRouteBindingState.Reserved,
            ActiveRevisionId: null,
            ActiveRevisionNumber: null,
            now,
            now);
        return new CreateResult(app, revision, binding);
    }

    public static string NormalizeRoute(string route) =>
        route.Trim().Trim('/').ToLowerInvariant();
}

public sealed record FunctionRevisionSpecInput(
    FunctionImage Image,
    string RuntimeProtocolVersion,
    FunctionConfiguration Configuration,
    FunctionResources Resources,
    FunctionScalingPolicy Scaling,
    ImmutableList<FunctionHandlerSpec> Handlers)
{
    public FunctionRevisionSpec ToRevisionSpec(string kubernetesNamespace) =>
        new(
            kubernetesNamespace,
            Image,
            RuntimeProtocolVersion,
            Configuration,
            Resources,
            Scaling,
            Handlers);
}
