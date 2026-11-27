// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

using System.Collections.Immutable;
using System.Text.Json.Serialization;
using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;
using Pole.Multitenancy.Primitives;

namespace Pole.Features.Functions.Domain;

public sealed record FunctionApp(
    [property: BsonId]
    [property: BsonRepresentation(BsonType.ObjectId)]
    string Id,
    string TenantId,
    string ResourceGroupId,
    FunctionPlacementIdentity Placement,
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
    long PlanningFence,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc
) : ITenantOwned;

public sealed record FunctionPlacementIdentity(
    string ResourceDeploymentId,
    string RuntimeCellId,
    string DeploymentTargetId
);

public enum FunctionAppDesiredState
{
    Enabled,
    Suspended,
    Deleting,
    Prepared,
}

public enum FunctionAppStatus
{
    Provisioning,
    Ready,
    Updating,
    Suspended,
    Failed,
    Deleting,
    Prepared,
}

public sealed record FunctionRevision(
    [property: BsonId]
    [property: BsonRepresentation(BsonType.ObjectId)]
    string Id,
    string TenantId,
    string FunctionAppId,
    int RevisionNumber,
    FunctionRevisionSpec Spec,
    FunctionRevisionStatus Status,
    long PlanningFence,
    DateTimeOffset CreatedAtUtc
) : ITenantOwned
{
    public static FunctionRevision CreateProvisioning(
        string id,
        string tenantId,
        string functionAppId,
        int revisionNumber,
        FunctionRevisionSpec spec,
        DateTimeOffset createdAtUtc) => new(
        id,
        tenantId,
        functionAppId,
        revisionNumber,
        spec,
        new ProvisioningFunctionRevisionStatus(),
        PlanningFence: 0,
        createdAtUtc);

    public FunctionRevision MarkReady(DateTimeOffset readyAtUtc)
    {
        if (Status is not ProvisioningFunctionRevisionStatus)
            throw new InvalidOperationException(
                $"Cannot mark Function revision ready from status '{Status.GetType().Name}'.");
        return this with { Status = new ReadyFunctionRevisionStatus(readyAtUtc) };
    }

    public FunctionRevision Activate()
    {
        if (Status is not ReadyFunctionRevisionStatus ready)
            throw new InvalidOperationException(
                $"Cannot activate Function revision from status '{Status.GetType().Name}'.");
        return this with { Status = new ActiveFunctionRevisionStatus(ready.ReadyAtUtc) };
    }

    public FunctionRevision Drain()
    {
        if (Status is not ActiveFunctionRevisionStatus active)
            throw new InvalidOperationException(
                $"Cannot drain Function revision from status '{Status.GetType().Name}'.");
        return this with { Status = new DrainingFunctionRevisionStatus(active.ReadyAtUtc) };
    }

    public FunctionRevision Retire()
    {
        if (Status is not DrainingFunctionRevisionStatus draining)
            throw new InvalidOperationException(
                $"Cannot retire Function revision from status '{Status.GetType().Name}'.");
        return this with { Status = new RetiredFunctionRevisionStatus(draining.ReadyAtUtc) };
    }

    public FunctionRevision Fail(FunctionRevisionFailure failure)
    {
        if (Status is not (ProvisioningFunctionRevisionStatus or ReadyFunctionRevisionStatus))
            throw new InvalidOperationException(
                $"Cannot fail Function revision from status '{Status.GetType().Name}'.");
        return this with { Status = new FailedFunctionRevisionStatus(failure) };
    }
}

[BsonDiscriminator(RootClass = true)]
[BsonKnownTypes(
    typeof(ProvisioningFunctionRevisionStatus),
    typeof(ReadyFunctionRevisionStatus),
    typeof(ActiveFunctionRevisionStatus),
    typeof(DrainingFunctionRevisionStatus),
    typeof(RetiredFunctionRevisionStatus),
    typeof(FailedFunctionRevisionStatus))]
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(ProvisioningFunctionRevisionStatus), "provisioning")]
[JsonDerivedType(typeof(ReadyFunctionRevisionStatus), "ready")]
[JsonDerivedType(typeof(ActiveFunctionRevisionStatus), "active")]
[JsonDerivedType(typeof(DrainingFunctionRevisionStatus), "draining")]
[JsonDerivedType(typeof(RetiredFunctionRevisionStatus), "retired")]
[JsonDerivedType(typeof(FailedFunctionRevisionStatus), "failed")]
public abstract record FunctionRevisionStatus;

[BsonDiscriminator("provisioning")]
public sealed record ProvisioningFunctionRevisionStatus : FunctionRevisionStatus;

[BsonDiscriminator("ready")]
public sealed record ReadyFunctionRevisionStatus(
    DateTimeOffset ReadyAtUtc
) : FunctionRevisionStatus;

[BsonDiscriminator("active")]
public sealed record ActiveFunctionRevisionStatus(
    DateTimeOffset ReadyAtUtc
) : FunctionRevisionStatus;

[BsonDiscriminator("draining")]
public sealed record DrainingFunctionRevisionStatus(
    DateTimeOffset ReadyAtUtc
) : FunctionRevisionStatus;

[BsonDiscriminator("retired")]
public sealed record RetiredFunctionRevisionStatus(
    DateTimeOffset ReadyAtUtc
) : FunctionRevisionStatus;

[BsonDiscriminator("failed")]
public sealed record FailedFunctionRevisionStatus(
    FunctionRevisionFailure Failure
) : FunctionRevisionStatus;

public sealed record FunctionRevisionSpec(
    FunctionImage Image,
    string RuntimeProtocolVersion,
    FunctionConfiguration Configuration,
    FunctionResources Resources,
    FunctionScalingPolicy Scaling,
    ImmutableList<FunctionHandlerSpec> Handlers
);

public sealed record FunctionImage(
    string Server,
    string Name,
    string Tag,
    string? Digest,
    string? PullSecretName
)
{
    [BsonIgnore] public string Reference => Digest is null ? $"{Server}/{Name}:{Tag}" : $"{Server}/{Name}@{Digest}";
}

public sealed record FunctionConfiguration(
    ImmutableDictionary<string, string> EnvironmentVariables,
    ImmutableDictionary<string, FunctionSecretReference> SecretReferences
);

public sealed record FunctionSecretReference(
    [property: BsonRepresentation(BsonType.ObjectId)]
    string SecretId,
    string Key
);

public sealed record FunctionResources(
    FunctionResourceValues Requests,
    FunctionResourceValues Limits
);

public sealed record FunctionResourceValues(
    int CpuMillis,
    int MemoryMi
);

public sealed record FunctionScalingPolicy(
    int MinInstances,
    int MaxInstances,
    int TargetConcurrencyPerInstance,
    int MaxConcurrencyPerInstance,
    int ScaleDownDelaySeconds,
    int ActivationTimeoutSeconds
)
{
    public const int MinimumActivationTimeoutSeconds = 1;
    public const int MaximumActivationTimeoutSeconds = 120;
}

public sealed record FunctionHandlerSpec(
    string Name,
    int MaxConcurrencyPerInstance,
    FunctionTriggerSpec Trigger
);

[BsonDiscriminator(RootClass = true)]
[BsonKnownTypes(typeof(HttpFunctionTriggerSpec), typeof(TimerFunctionTriggerSpec))]
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(HttpFunctionTriggerSpec), "http")]
[JsonDerivedType(typeof(TimerFunctionTriggerSpec), "timer")]
public abstract record FunctionTriggerSpec(
    string TriggerId
);

[BsonDiscriminator("http")]
public sealed record HttpFunctionTriggerSpec(
    string TriggerId,
    string Route,
    ImmutableHashSet<string> Methods,
    FunctionExposure Exposure
) : FunctionTriggerSpec(TriggerId);

[BsonDiscriminator("timer")]
public sealed record TimerFunctionTriggerSpec(
    string TriggerId,
    string Cron,
    string TimeZone,
    TimerConcurrencyPolicy ConcurrencyPolicy,
    TimerMisfirePolicy MisfirePolicy,
    int ScheduleVersion,
    FunctionRetryPolicy Retry
) : FunctionTriggerSpec(TriggerId);

public enum FunctionExposure
{
    Internal,
    Public,
}

public enum TimerConcurrencyPolicy
{
    Allow,
    Forbid,
}

public enum TimerMisfirePolicy
{
    Skip,
    FireOnce,
}

public sealed record FunctionRetryPolicy(
    int MaxAttempts,
    ImmutableList<int> BackoffSeconds
);

public sealed record FunctionRevisionFailure(
    string Code,
    string Message
);

public sealed record FunctionRouteBinding(
    [property: BsonId] string NormalizedRoute,
    string TenantId,
    string FunctionAppId,
    ImmutableList<FunctionHttpRouteBinding> HttpRoutes,
    FunctionRouteBindingState State,
    string? ActiveRevisionId,
    int? ActiveRevisionNumber,
    long PlanningFence,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc
) : ITenantOwned;

public static class FunctionRouteBindingFactory
{
    public static FunctionRouteBinding Reserve(FunctionApp app, DateTimeOffset now) => new(
        app.FunctionRoute,
        app.TenantId,
        app.Id,
        [],
        FunctionRouteBindingState.Reserved,
        ActiveRevisionId: null,
        ActiveRevisionNumber: null,
        PlanningFence: 0,
        now,
        now);
}

public sealed record FunctionHttpRouteBinding(
    string TriggerId,
    string HandlerName,
    string HandlerRoute,
    ImmutableHashSet<string> Methods,
    FunctionExposure Exposure
);

public enum FunctionRouteBindingState
{
    Reserved,
    Active,
    Disabled,
}

public sealed record FunctionQuotaAllocation(
    long FunctionAppCount,
    long TotalCpuMillis,
    long TotalMemoryMi,
    long WorkloadInstances
)
{
    public static FunctionQuotaAllocation FromRevision(FunctionRevisionSpec spec) => new(
        1,
        checked((long)spec.Resources.Requests.CpuMillis * spec.Scaling.MaxInstances),
        checked((long)spec.Resources.Requests.MemoryMi * spec.Scaling.MaxInstances),
        spec.Scaling.MaxInstances);

    public static FunctionQuotaAllocation Peak(FunctionQuotaAllocation first, FunctionQuotaAllocation second) => new(
        Math.Max(first.FunctionAppCount, second.FunctionAppCount),
        Math.Max(first.TotalCpuMillis, second.TotalCpuMillis),
        Math.Max(first.TotalMemoryMi, second.TotalMemoryMi),
        Math.Max(first.WorkloadInstances, second.WorkloadInstances));
}

public static class FunctionAppFactory
{
    public static CreateOptions DefaultCreateOptions =>
        new(ObjectId.GenerateNewId().ToString(), ObjectId.GenerateNewId().ToString());

    public static CreateResult Create(
        string tenantId,
        string resourceGroupId,
        FunctionPlacementIdentity placement,
        string name,
        string functionRoute,
        FunctionRevisionSpecInput spec,
        ImmutableHashSet<string> tags,
        DateTimeOffset now,
        CreateOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(placement);
        options ??= DefaultCreateOptions;
        var functionAppId = options.FunctionAppId;
        var revisionId = options.RevisionId;
        var normalizedRoute = NormalizeRoute(functionRoute);
        var revisionSpec = spec.ToRevisionSpec() with
        {
            Handlers = spec.Handlers
                .Select(handler => handler with
                {
                    Trigger = handler.Trigger switch
                    {
                        HttpFunctionTriggerSpec http => http with
                        {
                            TriggerId = Guid.NewGuid().ToString("N"),
                        },
                        TimerFunctionTriggerSpec timer => timer with
                        {
                            TriggerId = Guid.NewGuid().ToString("N"),
                            ScheduleVersion = 1,
                        },
                        _ => throw new InvalidOperationException("Unknown Function trigger spec."),
                    },
                })
                .ToImmutableList(),
        };
        var app = new FunctionApp(
            functionAppId,
            tenantId,
            resourceGroupId,
            placement,
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
            PlanningFence: 0,
            now,
            now);
        var revision = FunctionRevision.CreateProvisioning(
            revisionId,
            tenantId,
            functionAppId,
            revisionNumber: 1,
            revisionSpec,
            now);
        var binding = FunctionRouteBindingFactory.Reserve(app, now);
        return new CreateResult(app, revision, binding);
    }

    public static FunctionApp CreatePrepared(
        string tenantId,
        string resourceGroupId,
        FunctionPlacementIdentity placement,
        string name,
        string functionRoute,
        DateTimeOffset now,
        CreateOptions options) => new(
        options.FunctionAppId,
        tenantId,
        resourceGroupId,
        placement,
        name,
        name.Trim().ToLowerInvariant(),
        NormalizeRoute(functionRoute),
        ActiveRevisionId: null,
        PendingRevisionId: null,
        NextRevisionNumber: 1,
        FunctionAppDesiredState.Prepared,
        FunctionAppStatus.Prepared,
        new FunctionQuotaAllocation(1, 0, 0, 0),
        ImmutableHashSet<string>.Empty,
        PlanningFence: 0,
        now,
        now);

    public static string NormalizeRoute(string route) => route.Trim().Trim('/').ToLowerInvariant();

    public sealed record CreateOptions(
        string FunctionAppId,
        string RevisionId
    );

    public sealed record CreateResult(
        FunctionApp FunctionApp,
        FunctionRevision Revision,
        FunctionRouteBinding RouteBinding
    );
}

public sealed record FunctionRevisionSpecInput(
    FunctionImage Image,
    string RuntimeProtocolVersion,
    FunctionConfiguration Configuration,
    FunctionResources Resources,
    FunctionScalingPolicy Scaling,
    ImmutableList<FunctionHandlerSpec> Handlers
)
{
    public FunctionRevisionSpec ToRevisionSpec() => new(
        Image,
        RuntimeProtocolVersion,
        Configuration,
        Resources,
        Scaling,
        Handlers);
}
