using MassTransit;
using Microsoft.Extensions.Logging;
using MongoDB.Driver;
using Pole.Features.Functions.Application.Common;
using Pole.Features.Functions.Domain;
using Pole.Features.Functions.Events;
using Pole.Features.Functions.Runtime.Kubernetes;
using Pole.ResultType;

namespace Pole.Features.Functions.Runtime.Consumers;

public sealed class FunctionRevisionCreatedConsumer(
    IFunctionAppDbContext dbContext,
    IFunctionRevisionKubernetesManager kubernetesManager,
    IFunctionRevisionLifecycleService lifecycleService,
    ILogger<FunctionRevisionCreatedConsumer> logger)
    : IConsumer<FunctionRevisionCreatedEvent>
{
    public Task Consume(ConsumeContext<FunctionRevisionCreatedEvent> context) =>
        HandleAsync(context.Message, context.CancellationToken);

    public async Task HandleAsync(
        FunctionRevisionCreatedEvent message,
        CancellationToken cancellationToken = default)
    {
        var tenantId = message.EventCore.TenantId;
        var app = await dbContext.FunctionApps.Find(candidate =>
                candidate.TenantId == tenantId && candidate.Id == message.FunctionAppId)
            .FirstOrDefaultAsync(cancellationToken);
        var revision = await dbContext.FunctionRevisions.Find(candidate =>
                candidate.TenantId == tenantId &&
                candidate.FunctionAppId == message.FunctionAppId &&
                candidate.Id == message.RevisionId)
            .FirstOrDefaultAsync(cancellationToken);
        if (app is null || revision is null)
            throw new InvalidOperationException("Function App or revision no longer exists.");
        if (revision.State != FunctionRevisionState.Provisioning)
            return;

        var applied = await kubernetesManager.ApplyAsync(
            new FunctionRevisionInKubernetes(app, revision),
            cancellationToken);
        if (applied is ISuccess)
            return;

        var failure = (Failure)applied;
        logger.LogError(
            "[function-revision-provisioning-failed] Kubernetes could not apply revision {RevisionId} for Function App {FunctionAppId}: {Reason}",
            revision.Id,
            app.Id,
            failure.Message);
        var failed = await lifecycleService.FailAsync(
            tenantId,
            app.Id,
            revision.Id,
            new FunctionRevisionFailure("kubernetes-apply", failure.Message),
            cancellationToken);
        if (failed is IFailure completionFailure)
            throw new InvalidOperationException(completionFailure.Message);
    }
}

public static class FunctionRuntimeFeature;
