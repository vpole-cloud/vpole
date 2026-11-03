using Pole.Features.GitOps.Primitives;
using System.Text.RegularExpressions;
using static Pole.Features.WebServices.Application.Persistence.WebServiceDeclarationRules;
using Pole.Workloads;
using Pole.Database;
using System.Collections.Immutable;
using System.Text.Json;
using Pole.Features.Domains.Contracts;
using Pole.Features.ResourceConnections.Contracts;
using MongoDB.Bson;
using MongoDB.Driver;
using Pole.Features.ComputeProfiles.Contracts;
using Pole.Features.DeploymentTargets.Contracts;
using Pole.Features.Events.Abstractions;
using Pole.Features.GitOps.Contracts;
using Pole.Features.Quotas.Contracts;
using Pole.Features.ResourceDeployments.Contracts;
using Pole.Features.ResourceGroups.Contracts;
using Pole.Features.Secrets.Contracts;
using Pole.Features.SourceBuilds.Contracts;
using Pole.Features.WebServices.Application.Common;
using Pole.Features.WebServices.Application.Persistence.Mutations;
using Pole.Features.WebServices.Application.Persistence.Lifecycle;
using Pole.Features.WebServices.Application.Persistence.Releases;
using Pole.Features.WebServices.Domain;
using static Pole.Features.WebServices.Application.Persistence.WebServiceDeclarations;
using static Pole.Features.WebServices.Application.Persistence.WebServicePlanning;

namespace Pole.Features.WebServices.Application.Persistence;

internal sealed class WebServiceDeclarationExecution(
    IMongoCollection<WebService> services,
    IGitOpsMutations git,
    MongoRunTransaction planningTransactions,
    WebServicePlanningReader planningReader,
    IQuotaAdmissionExecutor quotas,
    IResourceRegistrar registrar,
    CreateResourceDeploymentSet createDeployments,
    RequestResourceDeploymentSetApply applyDeployments,
    AdmitConsumerSecretReferences admitSecrets,
    PersistCreatedWebService persistCreated,
    PersistUpdatedWebServiceOutcome persistUpdated,
    PersistCurrentWebServiceRelease persistRelease,
    WebServiceDeclarationIngress ingressPreparation,
    TimeProvider clock)
{
    public async Task<GitOperationResult> ApplyAsync(GitOperation operation, CancellationToken ct)
    {
        var spec = DeclarationJson.Read<WebServiceDeclaration>(operation.Declaration.Spec);
        var context = operation.Context;
        var loaded = await planningTransactions.InvokeOutcomeAsync<ReadWebServicePlanningOutcome>(async (tx, token) =>
            tx.Commit(await planningReader.ReadExecutionPlanningAsync(operation.Input, tx.Session, token)), ct);
        if (loaded is DeclarationReadBlocked blocked)
            return await BlockAsync(operation, blocked.Failure, ct);

        var dependencies = (WebServicePlanningDependencies)loaded.Value;
        var current = dependencies.Current;
        var deployments = dependencies.Placements?.Items ?? [];
        var compute = dependencies.Compute.Compute;
        var selectedTarget = dependencies.Target.Target;
        var binding = new WebServiceDeclaredPlacement(spec.Placement.DeploymentTarget, selectedTarget.Id,
            spec.Placement.ComputeProfile.Name, compute.ComputeProfileId);
        var domainSpec = ToSpec(spec, ImmutableDictionary<string, SecretEnvironmentReference>.Empty,
            current?.Spec, dependencies.ConnectionReferences, dependencies.BuildConfigurationId);
        var nextQuota = WebServiceDeploymentWorkflow.ToQuotaInput([(compute, spec.FixedReplicas)], domainSpec);
        QuotaMutation<WebServiceQuotaInput> quotaChange = current is null
            ? new CreateQuotaMutation<WebServiceQuotaInput>(nextQuota)
            : new UpdateQuotaMutation<WebServiceQuotaInput>(
                WebServiceQuotaInput.Create(deployments.Select(item => WebServiceDeploymentQuotaInput.Create(
                    item.Compute.Requests.CpuMillis, item.Compute.Requests.MemoryMi,
                    current.Spec.Autoscaling?.MaxReplicas ?? item.FixedReplicas))), nextQuota);
        var outcome = await quotas.ExecuteOutcomeAsync<WebServiceQuotaInput, GitOperationResult>(
            new ExecuteQuotaAdmissionRequest<global::Pole.Features.WebServices.Application.Common
                .WebServiceQuotaInput, global::Pole.Features.GitOps.Contracts.GitOperationResult>(
                TenantId: context.TenantId,
                ResourceKey: WebServiceQuotaResourceKey.Create(operation.ResourceId),
                Mutation: quotaChange,
                QuotaError: _ => new(
                    operation.ResourceId,
                    new WebServiceDeclarationRejected(WebServiceDeclarationError.QuotaUnavailable)),
                MutationCallback: async (tx, token) =>
                {
                    var rejected = await git.BeginAsync(operation, tx.Session, token);

                    if (rejected is not null)
                        return tx.Abort(rejected);

                    var groupId = await git.ResolveAcceptedAsync(
                        new ResolveAcceptedGitReferenceRequest(
                            Context: context,
                            ConsumerResourceId: operation.ResourceId,
                            Address: new("ResourceGroup", spec.ResourceGroup)),
                        session: tx.Session,
                        ct: token);

                    if (groupId is null)
                        return tx.Abort(new(
                            operation.ResourceId,
                            new WebServiceDeclarationRejected(
                                WebServiceDeclarationError.ResourceGroupUnavailable)));

                    GitOperationResult result;

                    if (current is null && await services.Find(
                            tx.Session,
                            WebServiceFilters.ById(context.TenantId, operation.ResourceId)).AnyAsync(token))
                        return tx.Abort(new(
                            operation.ResourceId,
                            new DeclarationPreconditionsChanged()));

                    if (current is null)
                        result = await PrepareAsync(
                            operation,
                            spec,
                            domainSpec,
                            dependencies.Scope,
                            compute!,
                            selectedTarget!,
                            binding,
                            tx.Session,
                            token);
                    else if (groupId != current.ResourceGroupId ||
                             dependencies.Scope.IsolationScopeId != current.IsolationScopeId)
                        return tx.Abort(new(
                            operation.ResourceId,
                            new WebServiceDeclarationRejected(
                                WebServiceDeclarationError.ImmutablePlacementChange)));
                    else
                        result = await ConfigureAsync(
                            operation,
                            spec,
                            current,
                            dependencies,
                            compute,
                            binding,
                            tx.Session,
                            token);

                    if (!result.Applied)
                        return tx.Abort(result);

                    var completed = await git.CompleteAsync(
                        new CompleteGitOperationRequest(
                            Operation: operation,
                            Result: result),
                        session: tx.Session,
                        ct: token);

                    return completed.Applied ? tx.Commit(completed) : tx.Abort(completed);
                }),
            cancellationToken: ct);

        return outcome.Applied || outcome.Failure is DeclarationPreconditionsChanged or StaleGitExecution
            ? outcome : await BlockAsync(operation, outcome.Failure!, ct);
    }

    private Task<GitOperationResult> BlockAsync(
        GitOperation operation,
        IDeclarationFailure failure,
        CancellationToken ct) =>
        git.ExecuteAsync(
            operation,
            (_, _) => Task.FromResult(new GitOperationResult(operation.ResourceId, failure)),
            ct);

    private async Task<GitOperationResult> PrepareAsync(GitOperation operation,
        WebServiceDeclaration spec,
        WebServiceSpec domainSpec,
        IsolationScopePlanningSnapshot scope,
        ResolvedComputeProfile compute,
        DeploymentTargetLookupDto target,
        WebServiceDeclaredPlacement binding,
        IClientSessionHandle session,
        CancellationToken ct)
    {
        var groupId = scope.ResourceGroupId;

        var service = WebServiceFactory.Create(operation.Declaration.Address.Name,
            operation.Context.TenantId,
            groupId,
            scope.IsolationScopeId,
            domainSpec,
            WebServiceExposure.Create(),
            WebServiceFactory.DefaultOptions with
            {
                Id = operation.ResourceId,
                Tags = spec.Tags.ToImmutableHashSet(),
                Lifecycle = new PreparedWebServiceLifecycle()
            }).BindDeclaredPlacement(binding);

        if (await registrar.RegisterAsync(
                new RegisterResourceRequest(
                    TenantId: service.TenantId,
                    ResourceGroupId: groupId,
                    Resource: new(WebServiceResourceDeployment.ResourceType, service.Id, service.Name)),
                session: session,
                cancellationToken: ct) is ResourceRegistrationMutationError)
            return new(
                service.Id,
                new WebServiceDeclarationRejected(WebServiceDeclarationError.ResourceRegistrationUnavailable));

        var created = await createDeployments(new([
                new(ObjectId.GenerateNewId().ToString(),
                    service.TenantId,
                    WebServiceResourceDeployment.ResourceType,
                    service.Id,
                    groupId,
                    service.IsolationScopeId,
                    new(
                        target.Id,
                        target.WorkloadConfiguration.Revision,
                        null),
                    compute.ToResourceDeploymentConfiguration(),
                    null,
                    ResourcePlacementCapability.MultiTarget,
                    spec.FixedReplicas,
                    service.ActiveRevision,
                    DeferredActivation: true,
                    PinnedCompute: true)
            ]),
            session,
            ct);

        if (created is not CreatedResourceDeployments)
            return new(service.Id, new WebServiceDeclarationRejected(WebServiceDeclarationError.PlacementUnavailable));

        var persisted = await persistCreated(session, service, service.ToCreateEvent([]), ct);

        if (persisted.Value is WebServiceNameAlreadyExists)
            return new(
                service.Id,
                new WebServiceDeclarationRejected(WebServiceDeclarationError.ExplicitAdoptionRequired));

        return new(service.Id, Changed: true);
    }

    private async Task<GitOperationResult> ConfigureAsync(GitOperation operation,
        WebServiceDeclaration spec,
        WebService current,
        WebServicePlanningDependencies dependencies,
        ResolvedComputeProfile compute,
        WebServiceDeclaredPlacement binding,
        IClientSessionHandle session,
        CancellationToken ct)
    {
        var authoritative = await services.Find(
            session,
            WebServiceFilters.ById(current.TenantId, current.Id)).FirstOrDefaultAsync(ct);

        if (authoritative is null || authoritative.Revision != current.Revision)
            return new(
                current.Id,
                new DeclarationPreconditionsChanged());

        current = authoritative;
        var placements = dependencies.Placements!.Items;
        var references = ImmutableDictionary.CreateBuilder<string, SecretEnvironmentReference>();
        foreach (var pair in spec.SecretEnvironment)
        {
            var secretId = await git.ResolveAcceptedAsync(
                new ResolveAcceptedGitReferenceRequest(
                    Context: operation.Context,
                    ConsumerResourceId: operation.ResourceId,
                    Address: new("Secret", pair.Value.Secret)),
                session: session,
                ct: ct);

            if (secretId is null)
                return new(current.Id, new WebServiceDeclarationRejected(WebServiceDeclarationError.SecretUnavailable));

            references.Add(pair.Key, new(secretId, pair.Value.Key));
        }

        var desired = ToSpec(spec, references.ToImmutable(), current.Spec, dependencies.ConnectionReferences,
            dependencies.BuildConfigurationId);
        var admitted = await admitSecrets(
            new AdmitConsumerSecretReferencesRequest(
                TenantId: current.TenantId,
                ResourceGroupId: current.ResourceGroupId,
                IsolationScopeId: current.IsolationScopeId,
                ConsumerType: WebServiceResourceDeployment.ResourceType,
                ConsumerId: current.Id,
                PreviousReferences: current.Spec.Config.SecretEnvironmentReferences.Values.Select(reference =>
                    new ConsumerSecretKeyReference(
                        reference.SecretId,
                        reference.Key)).ToArray(),
                DesiredReferences: desired.Config.SecretEnvironmentReferences.Values.Select(reference =>
                    new ConsumerSecretKeyReference(
                        reference.SecretId,
                        reference.Key)).ToArray(),
                Availability: ConsumerSecretAvailabilityPolicy.RequireAvailable,
                Retention: PreviousSecretReferenceRetention.KeepUntilRolloutConfirmed),
            session: session,
            ct: ct);

        if (admitted is ConsumerSecretReferenceUnavailable unavailable)
            return new(current.Id, new WebServiceSecretReferenceRejected(unavailable));

        var desiredExposure = current.Exposure with
        { Mode = spec.Exposure == PublicExposure ? WebServiceExposureMode.Public : WebServiceExposureMode.Private };
        var awaiting = desired.Image is PendingSourceBuildImage;
        var next = (awaiting
            ? current.AcceptConfigurationAwaitingArtifact(desired, spec.Tags, desiredExposure)
            : current.ActivatePrepared(desired, spec.Tags, desiredExposure)).BindDeclaredPlacement(binding);
        var replacementCompute = compute.ToResourceDeploymentConfiguration();
        var placementChanged = placements.Any(item => item.FixedReplicas != spec.FixedReplicas ||
            item.State == ResourceDeploymentPlanningState.Prepared && !awaiting ||
            IngressProjection(item.Ingress) != DesiredIngress(spec, dependencies.Target.Target) ||
            item.Compute.ComputeProfileId != replacementCompute.ComputeProfileId ||
            item.Compute.ComputeProfileRevision != replacementCompute.ComputeProfileRevision ||
            !JsonElement.DeepEquals(ComputeProjection(item.Compute), ComputeProjection(replacementCompute)));

        if (next == current && !placementChanged)
            return new(current.Id);

        if (next == current)
            next = current.AdvancePlacementDesiredState();

        var exposure = await ingressPreparation.PrepareIngressAsync(
            new(next, dependencies.Target.Target, placements), session, ct);
        if (exposure is WebServiceDeclarationRejected rejected)
            return new(current.Id, rejected);
        var ingress = ((PreparedWebServiceIngress)exposure.Value).Changes;

        var applied = await applyDeployments(new(current.TenantId,
                WebServiceResourceDeployment.ResourceType,
                current.Id,
                next.ActiveRevision,
                placements.Select(item => new ResourceDeploymentSetApplyItem(item.DeploymentId,
                    item.Revision,
                    new(item.Compute.ComputeProfileId, item.Compute.ComputeProfileRevision),
                    ingress[item.DeploymentId],
                    replacementCompute,
                    spec.FixedReplicas,
                    PinnedCompute: true)).ToArray(),
                awaiting ? ResourceDeploymentActivation.KeepPrepared : ResourceDeploymentActivation.Reconcile),
            session,
            ct);

        if (applied is ResourceDeploymentMutationError
            {
                Value: ResourceDeploymentChanged or
                ResourceDeploymentComputeConfigurationChanged or ResourceDeploymentIngressConfigurationChanged
            })
            return new(current.Id, new DeclarationPreconditionsChanged());

        if (applied is not MutatedResourceDeployments updated)
            return new(current.Id, new WebServiceDeclarationRejected(WebServiceDeclarationError.PlacementChanged));

        var persisted = await persistUpdated(
            session,
            current,
            next,
            next.ToUpdatedEvent(WebServiceDeploymentWorkflow.ToEventDeployments(updated.Placements)),
            ct,
            operation.Context);

        if (persisted is WebServiceDependencyUnavailable dependency)
            return new(current.Id, new WebServiceDependencyRejected(dependency));

        if (persisted is WebServiceUpdateConflict conflict)
            return new(current.Id, conflict.Reason == WebServiceUpdateConflictReason.ExpectedRevisionConflict
                ? new DeclarationPreconditionsChanged() : new GitDeclarationOwnershipConflict());

        if (persisted is WebServiceConnectionConflict)
            return new(current.Id, new WebServiceDeclarationRejected(WebServiceDeclarationError.PlacementChanged));

        if (awaiting)
            return new(current.Id, Changed: true);

        var eventCore = EventCore.Create(current.TenantId);
        var release = WebServiceReleaseFactory.CreatePending(current.TenantId,
            current.Id,
            next.ActiveRevision,
            current.Lifecycle is PreparedWebServiceLifecycle
                ? WebServiceReleaseAction.Create
                : WebServiceReleaseAction.Update,
            null,
            null,
            next.Spec,
            WebServiceDeploymentWorkflow.ToReleaseDeployments(updated.Placements),
            eventCore.TraceId,
            eventCore.CorrelationId,
            new(ObjectId.GenerateNewId().ToString(), clock.GetUtcNow().UtcDateTime),
            next.Exposure,
            new GitOpsReleaseProvenance(operation.Context.SourceId,
                operation.Context.RunId,
                operation.Context.Authority.Commit,
                current.Spec.Image.ResolvedImage?.Tag,
                next.Spec.Image.ResolvedImage!.Tag));
        await persistRelease(session, release, ct);

        return new(current.Id, Changed: true);
    }
}
