using System.Collections.Immutable;
using Pole.Features.Events.Abstractions;
using Pole.Features.Functions.Domain;
using Pole.Features.Functions.Events;

namespace Pole.Features.Functions.Application.Common;

internal static class FunctionAppEventExtensions
{
    public static FunctionAppCreatedEvent ToCreatedEvent(this FunctionApp app, FunctionRevision revision) => new(
        app.Id,
        revision.Id,
        revision.RevisionNumber,
        app.ResourceGroupId,
        app.Placement.ToSnapshot(),
        app.Name,
        app.FunctionRoute,
        revision.Spec.ToSnapshot())
    {
        EventCore = EventCore.Create(app.TenantId),
    };

    public static FunctionRevisionCreatedEvent
        ToRevisionCreatedEvent(this FunctionRevision revision, FunctionApp app) => new(
        revision.FunctionAppId,
        revision.Id,
        revision.RevisionNumber,
        app.Placement.ToSnapshot(),
        revision.Spec.ToSnapshot())
    {
        EventCore = EventCore.Create(revision.TenantId),
    };

    public static FunctionRevisionPromotedEvent ToPromotedEvent(
        this FunctionRevision revision,
        FunctionApp app,
        string? previousRevisionId) => new(
        revision.FunctionAppId,
        revision.Id,
        revision.RevisionNumber,
        app.Placement.ToSnapshot(),
        previousRevisionId)
    {
        EventCore = EventCore.Create(revision.TenantId),
    };

    public static FunctionRevisionFailedEvent ToFailedEvent(
        this FunctionRevision revision,
        FunctionApp app,
        FunctionRevisionFailure failure) => new(
        revision.FunctionAppId,
        revision.Id,
        revision.RevisionNumber,
        app.Placement.ToSnapshot(),
        failure.Code,
        failure.Message)
    {
        EventCore = EventCore.Create(revision.TenantId),
    };

    public static FunctionRevisionRetiredEvent ToRetiredEvent(this FunctionRevision revision, FunctionApp app) => new(
        revision.FunctionAppId,
        revision.Id,
        revision.RevisionNumber,
        app.Placement.ToSnapshot())
    {
        EventCore = EventCore.Create(revision.TenantId),
    };

    public static FunctionRevisionSpecSnapshot ToSnapshot(this FunctionRevisionSpec spec) => new(
        new FunctionImageSnapshot(
            spec.Image.Server,
            spec.Image.Name,
            spec.Image.Tag,
            spec.Image.Digest,
            spec.Image.PullSecretName),
        spec.RuntimeProtocolVersion,
        new FunctionConfigurationSnapshot(
            spec.Configuration.EnvironmentVariables,
            spec.Configuration.SecretReferences.ToImmutableDictionary(
                pair => pair.Key,
                pair => new FunctionSecretReferenceSnapshot(pair.Value.SecretId, pair.Value.Key))),
        new FunctionResourcesSnapshot(
            new FunctionResourceValuesSnapshot(spec.Resources.Requests.CpuMillis, spec.Resources.Requests.MemoryMi),
            new FunctionResourceValuesSnapshot(spec.Resources.Limits.CpuMillis, spec.Resources.Limits.MemoryMi)),
        new FunctionScalingPolicySnapshot(
            spec.Scaling.MinInstances,
            spec.Scaling.MaxInstances,
            spec.Scaling.TargetConcurrencyPerInstance,
            spec.Scaling.MaxConcurrencyPerInstance,
            spec.Scaling.ScaleDownDelaySeconds,
            spec.Scaling.ActivationTimeoutSeconds),
        spec.Handlers
            .Select(handler => new FunctionHandlerSnapshot(
                handler.Name,
                handler.MaxConcurrencyPerInstance,
                handler.Trigger switch
                {
                    HttpFunctionTriggerSpec http => new HttpFunctionTriggerSnapshot(
                        http.TriggerId,
                        http.Route,
                        http.Methods,
                        http.Exposure.ToString()),
                    TimerFunctionTriggerSpec timer => new TimerFunctionTriggerSnapshot(
                        timer.TriggerId,
                        timer.Cron,
                        timer.TimeZone,
                        timer.ConcurrencyPolicy.ToString(),
                        timer.MisfirePolicy.ToString(),
                        timer.ScheduleVersion,
                        new FunctionRetryPolicySnapshot(timer.Retry.MaxAttempts, timer.Retry.BackoffSeconds)),
                    _ => throw new InvalidOperationException("Unknown Function trigger spec."),
                }))
            .ToImmutableList());

    public static FunctionPlacementSnapshot ToSnapshot(this FunctionPlacementIdentity placement) => new(
        placement.ResourceDeploymentId,
        placement.RuntimeCellId,
        placement.DeploymentTargetId);
}
