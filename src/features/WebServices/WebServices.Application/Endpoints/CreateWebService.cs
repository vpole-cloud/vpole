using System.Collections.Immutable;
using System.Text.Json.Serialization;
using FluentValidation;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using MongoDB.Bson;
using Pole.ASP.Helpers;
using Pole.Database;
using Pole.Features.ComputeProfiles.Contracts;
using Pole.Features.Core.Multitenancy;
using Pole.Features.DeploymentTargets.Contracts;
using Pole.Features.Domains.Contracts;
using Pole.Features.Events.Abstractions;
using Pole.Features.Quotas.Contracts;
using Pole.Features.ResourceDeployments.Contracts;
using Pole.Features.ResourceGroups.Contracts;
using Pole.Features.WebServices.Application.Common;
using Pole.Features.WebServices.Application.Persistence;
using Pole.Features.WebServices.Application.Persistence.Mutations;
using Pole.Features.WebServices.Application.Persistence.Releases;
using Pole.Features.WebServices.Domain;
using Pole.Mediator;
using Pole.ResultType;
using Pole.Validation;
using Pole.Workloads;

using static Pole.Features.WebServices.Application.Common.WebServiceHttpMappings;

namespace Pole.Features.WebServices.Application.Endpoints;

public static class CreateWebService
{
    extension(IEndpointRouteBuilder app)
    {
        public IEndpointRouteBuilder MapCreateWebServiceEndpoint()
        {
            app.MapPost("/", async (
                    IMediator mediator,
                    [AsParameters] CreateWebServiceRequest request,
                    CancellationToken cancellationToken) =>
                    (await mediator.Send(request, cancellationToken))
                    .ToHttpResult(outcome => outcome switch
                    {
                        CreateWebServiceResponse value => Results.Accepted(value: value),
                        CreateWebServiceError error => MapError(error)
                    }))
                .WithName("CreateWebService")
                .Produces<CreateWebServiceResponse>(StatusCodes.Status202Accepted);

            return app;
        }
    }
}

public sealed record CreateWebServiceTargetUnavailable(string DeploymentTargetId);

public union CreateWebServicePlacementError(
    NoDeploymentTargetAcceptsWorkloads,
    NoDeploymentTargetAcceptsPublicWorkloads,
    DeploymentTargetSelectionRequired,
    CreateWebServiceTargetUnavailable);

public union CreateWebServiceError(
    WebServiceNameAlreadyExists,
    ResourceGroupOwnershipError,
    CreateWebServicePlacementError,
    ComputeProfileResolutionError,
    GeneratedDomainZoneError,
    GeneratedHostnameBindingError,
    CreateResourceDeploymentError,
    ResourceRegistrationMutationError,
    QuotaExecutionError);

public union CreateWebServiceOutcome(
    CreateWebServiceResponse,
    CreateWebServiceError);

public sealed class CreateWebServiceHandler(
    GetResourceGroupOwnership getResourceGroupOwnership,
    ListEligibleDeploymentTargetsForNewWorkload listEligibleWorkloads,
    ListEligiblePublicIngressDeploymentTargetsForNewWorkload listEligibleTargets,
    ResolveComputeProfiles resolveComputeProfiles,
    IQuotaAdmissionExecutor quotaAdmissionExecutor,
    GetOrCreateGeneratedDomainZones getOrCreateGeneratedDomainZones,
    CreateGeneratedHostnameBindings createGeneratedHostnameBindings,
    CreateResourceDeploymentSet createResourceDeploymentSet,
    IResourceRegistrar resourceRegistrar,
    PersistCreatedWebService persistCreatedWebService,
    PersistCurrentWebServiceRelease persistCurrentWebServiceRelease,
    TimeProvider timeProvider)
    : IMediatorOutcomeHandler<CreateWebServiceRequest, CreateWebServiceOutcome>
{
    public async Task<CreateWebServiceOutcome> HandleAsync(
        CreateWebServiceRequest request,
        CancellationToken cancellationToken = default)
    {
        var ownership = await getResourceGroupOwnership(
            request.TenantId,
            request.Body.ResourceGroupId,
            cancellationToken);
        if (ownership is ResourceGroupOwnershipError ownershipError)
            return Error(new CreateWebServiceError(ownershipError));

        var resourceGroup = (ResourceGroupOwnership)ownership.Value;

        var isolationScopeId = request.Body.Placement.IsolationScopeId
            ?? resourceGroup.DefaultIsolationScopeId;
        var targetsResult = await ResolveTargetsAsync(
            request.TenantId,
            isolationScopeId,
            request.Body.Placement.Targets,
            request.Body.Exposure,
            cancellationToken);
        if (targetsResult is CreateWebServicePlacementError targetsError)
            return Error(new CreateWebServiceError(targetsError));

        var targets = (IReadOnlyList<ResolvedTargetRequest>)targetsResult.Value;
        var computeResult = await resolveComputeProfiles(
            new ComputeProfileBatchResolutionRequest(
                request.TenantId,
                WebServiceResourceDeployment.ResourceType,
                targets.Select(target => new ComputeProfileTargetResolutionRequest(
                    target.Target.DeploymentTargetId,
                    target.Request.ComputeProfileId)).ToArray()),
            cancellationToken);
        if (computeResult is ComputeProfileResolutionError computeError)
            return Error(new CreateWebServiceError(computeError));

        var computes = (IReadOnlyList<ResolvedComputeProfile>)computeResult.Value;
        var computeByTarget = computes.ToDictionary(
            compute => compute.DeploymentTargetId,
            StringComparer.Ordinal);
        if (computeByTarget.Count != targets.Count || targets.Any(target =>
                !computeByTarget.ContainsKey(target.Target.DeploymentTargetId)))
        {
            throw new InvalidOperationException(
                "Compute profile batch resolution did not return one result for every selected deployment target.");
        }

        var options = WebServiceFactory.DefaultOptions with
        {
            Tags = request.Body.Tags,
            Lifecycle = request.Body.CreationMode == WebServiceCreationMode.SourceBuild
                ? new PreparedWebServiceLifecycle() : new ActiveWebServiceLifecycle()
        };
        var spec = request.Body.Spec.ToDomain();
        var quotaInput = WebServiceDeploymentWorkflow.ToQuotaInput(
            targets.Select(target => (
                computeByTarget[target.Target.DeploymentTargetId],
                target.Request.FixedReplicas)),
            spec);

        return await quotaAdmissionExecutor.ExecuteOutcomeAsync<
            WebServiceQuotaInput,
            CreateWebServiceOutcome>(
            new ExecuteQuotaAdmissionRequest<global::Pole.Features.WebServices.Application.Common.WebServiceQuotaInput, global::Pole.Features.WebServices.Application.Endpoints.CreateWebServiceOutcome>(
                TenantId: request.TenantId,
                ResourceKey: WebServiceQuotaResourceKey.Create(options.Id),
                Mutation: new CreateQuotaMutation<WebServiceQuotaInput>(quotaInput),
                QuotaError: quotaError => Error(new CreateWebServiceError(quotaError)),
                MutationCallback: (transaction, ct) => PersistAsync(
                request,
                isolationScopeId,
                spec,
                options,
                targets,
                computeByTarget,
                transaction,
                ct)),
            cancellationToken: cancellationToken);
    }

    private async Task<ResolveTargetsOutcome> ResolveTargetsAsync(
        string tenantId,
        string isolationScopeId,
        IReadOnlyList<WebServiceDeploymentTargetRequest> requestedTargets,
        WebServiceExposureMode exposureMode,
        CancellationToken cancellationToken)
    {
        var eligible = exposureMode is WebServiceExposureMode.Public
            ? (await listEligibleTargets(
                new DeploymentTargetAdmissionRequest(
                    tenantId,
                    WebServiceResourceDeployment.ResourceType,
                    isolationScopeId,
                    ExplicitDeploymentTargetId: null,
                    DeploymentTargetRequiredCapabilities.PublicIngress),
                cancellationToken))
                .Select(target => new EligibleTarget(
                    target.DeploymentTargetId,
                    target.WorkloadConfigurationRevision,
                    ResourceDeploymentIngressConfiguration.Create(
                        target.IngressClassName,
                        target.ClusterIssuerName,
                        target.PublicBaseDomain,
                        [])))
                .ToArray()
            : (await listEligibleWorkloads(cancellationToken))
                .Where(target => target.AcceptsWorkloads && !target.DeletionRequested && target.Enabled)
                .Select(target => new EligibleTarget(
                    target.Id,
                    target.WorkloadConfiguration.Revision,
                    null))
                .ToArray();

        var byId = eligible.ToDictionary(target => target.DeploymentTargetId, StringComparer.Ordinal);
        var resolved = new List<ResolvedTargetRequest>(requestedTargets.Count);
        foreach (var requested in requestedTargets)
        {
            EligibleTarget target;
            if (string.IsNullOrWhiteSpace(requested.DeploymentTargetId))
            {
                if (requestedTargets.Count != 1)
                    return PlacementError(new DeploymentTargetSelectionRequired(
                        eligible.Select(item => item.DeploymentTargetId).ToArray()));
                if (eligible.Length == 0)
                    return exposureMode is WebServiceExposureMode.Public
                        ? PlacementError(new NoDeploymentTargetAcceptsPublicWorkloads())
                        : PlacementError(new NoDeploymentTargetAcceptsWorkloads());
                if (eligible.Length > 1)
                    return PlacementError(new DeploymentTargetSelectionRequired(
                        eligible.Select(item => item.DeploymentTargetId).ToArray()));
                target = eligible[0];
            }
            else if (!byId.TryGetValue(requested.DeploymentTargetId.Trim(), out target!))
            {
                return PlacementError(new CreateWebServiceTargetUnavailable(
                    requested.DeploymentTargetId.Trim()));
            }

            resolved.Add(new ResolvedTargetRequest(
                requested,
                new ResolvedResourceDeploymentTarget(
                    target.DeploymentTargetId,
                    target.WorkloadConfigurationRevision,
                    target.Ingress)));
        }

        return (IReadOnlyList<ResolvedTargetRequest>)resolved;
    }

    private async Task<CreateDeploymentRequestsOutcome> CreateDeploymentRequestsAsync(
        WebService webService,
        IReadOnlyList<ResolvedTargetRequest> targets,
        IReadOnlyDictionary<string, ResolvedComputeProfile> computeByTarget,
        MongoDB.Driver.IClientSessionHandle session,
        CancellationToken cancellationToken)
    {
        var deployments = targets.Select(target => new PendingDeploymentRequest(
            target,
            ObjectId.GenerateNewId().ToString())).ToArray();
        IReadOnlyList<HostnameBindingSnapshot> bindings = [];
        if (webService.Exposure.Mode is WebServiceExposureMode.Public)
        {
            var zoneRequests = deployments.Select(pending =>
            {
                var target = pending.Target.Target;
                var ingress = target.Ingress!;
                return new GetOrCreateGeneratedZoneRequest(
                    webService.TenantId,
                    target.DeploymentTargetId,
                    ingress.PublicBaseDomain!,
                    target.WorkloadConfigurationRevision);
            }).ToArray();
            var zonesResult = await getOrCreateGeneratedDomainZones(
                zoneRequests,
                session,
                cancellationToken);
            if (zonesResult is GeneratedDomainZoneError zonesError)
                return new CreateWebServiceError(zonesError);

            var zones = (IReadOnlyList<TenantDomainZoneSnapshot>)zonesResult.Value;
            var bindingRequests = deployments.Select((pending, index) =>
                new CreateGeneratedHostnameBindingRequest(
                    webService.TenantId,
                    zones[index].Id,
                    pending.Target.Target.DeploymentTargetId,
                    WebServiceResourceDeployment.ResourceType,
                    webService.Id,
                    pending.DeploymentId,
                    webService.Exposure.GeneratedHostnameLabel(webService.Id))).ToArray();
            var bindingsResult = await createGeneratedHostnameBindings(
                bindingRequests,
                session,
                cancellationToken);
            if (bindingsResult is GeneratedHostnameBindingError bindingsError)
                return new CreateWebServiceError(bindingsError);
            bindings = (IReadOnlyList<HostnameBindingSnapshot>)bindingsResult.Value;
        }

        var requests = new List<CreateResourceDeploymentRequest>(deployments.Length);
        for (var index = 0; index < deployments.Length; index++)
        {
            var pending = deployments[index];
            var target = pending.Target;
            var ingress = target.Target.Ingress;
            requests.Add(new CreateResourceDeploymentRequest(
                pending.DeploymentId,
                webService.TenantId,
                WebServiceResourceDeployment.ResourceType,
                webService.Id,
                webService.ResourceGroupId,
                webService.IsolationScopeId,
                target.Target with
                {
                    Ingress = ingress is null
                        ? null
                        : ResourceDeploymentIngressConfiguration.Create(
                            ingress.IngressClassName,
                            ingress.ClusterIssuerName,
                            ingress.PublicBaseDomain,
                            [bindings[index].Hostname])
                },
                computeByTarget[target.Target.DeploymentTargetId]
                    .ToResourceDeploymentConfiguration(),
                Storage: null,
                ResourcePlacementCapability.MultiTarget,
                target.Request.FixedReplicas,
                webService.ActiveRevision,
                DeferredActivation: webService.Lifecycle is PreparedWebServiceLifecycle));
        }

        return (IReadOnlyList<CreateResourceDeploymentRequest>)requests;
    }

    private async Task<QuotaAdmissionTransactionDecision<CreateWebServiceOutcome>> PersistAsync(
        CreateWebServiceRequest request,
        string isolationScopeId,
        WebServiceSpec spec,
        WebServiceFactory.CreateOptions options,
        IReadOnlyList<ResolvedTargetRequest> targets,
        IReadOnlyDictionary<string, ResolvedComputeProfile> computeByTarget,
        QuotaAdmissionTransactionContext<CreateWebServiceOutcome> transaction,
        CancellationToken cancellationToken)
    {
        var webService = WebServiceFactory.Create(
            request.Body.Name,
            request.TenantId,
            request.Body.ResourceGroupId,
            isolationScopeId,
            spec,
            WebServiceExposure.Create(request.Body.Exposure),
            options);

        var deploymentRequestsResult = await CreateDeploymentRequestsAsync(
            webService,
            targets,
            computeByTarget,
            transaction.Session,
            cancellationToken);
        if (deploymentRequestsResult is CreateWebServiceError requestsError)
            return Abort(transaction, requestsError);

        var deploymentRequests =
            (IReadOnlyList<CreateResourceDeploymentRequest>)deploymentRequestsResult.Value;
        var registration = await resourceRegistrar.RegisterAsync(
            new RegisterResourceRequest(
                TenantId: webService.TenantId,
                ResourceGroupId: webService.ResourceGroupId,
                Resource: new ResourceRegistrationDescriptor(
                WebServiceResourceDeployment.ResourceType,
                webService.Id,
                webService.Name)),
            session: transaction.Session,
            cancellationToken: cancellationToken);
        if (registration is ResourceRegistrationMutationError registrationError)
            return Abort(transaction, new CreateWebServiceError(registrationError));

        var deploymentsResult = await createResourceDeploymentSet(
            new CreateResourceDeploymentSetRequest(deploymentRequests),
            transaction.Session,
            cancellationToken);
        if (deploymentsResult is CreateResourceDeploymentError deploymentsError)
            return Abort(transaction, new CreateWebServiceError(deploymentsError));
        var deployments = ((CreatedResourceDeployments)deploymentsResult.Value).Placements;

        var eventCore = EventCore.Create(webService.TenantId);
        var createdEvent = webService.ToCreateEvent(
            WebServiceDeploymentWorkflow.ToEventDeployments(deployments));
        var persisted = await persistCreatedWebService(transaction.Session, webService, createdEvent, cancellationToken);
        if (persisted.Value is WebServiceNameAlreadyExists duplicate) return Abort(transaction, new CreateWebServiceError(duplicate));

        if (webService.Lifecycle is PreparedWebServiceLifecycle)
            return transaction.Commit(new CreateWebServiceOutcome(
                new CreateWebServiceResponse(webService.Id, deployments)));

        var release = WebServiceReleaseFactory.CreatePending(
            webService.TenantId,
            webService.Id,
            webService.ActiveRevision,
            WebServiceReleaseAction.Create,
            request.ActorUserId.Value,
            request.ActorEmail.Value,
            webService.Spec,
            WebServiceDeploymentWorkflow.ToReleaseDeployments(deployments),
            eventCore.TraceId,
            eventCore.CorrelationId,
            new WebServiceReleaseFactory.CreateOptions(
                ObjectId.GenerateNewId().ToString(),
                timeProvider.GetUtcNow().UtcDateTime),
            webService.Exposure);
        await persistCurrentWebServiceRelease(transaction.Session, release, cancellationToken);

        return transaction.Commit(new CreateWebServiceOutcome(
            new CreateWebServiceResponse(webService.Id, deployments)));
    }

    private static CreateWebServiceOutcome Error(CreateWebServiceError error) =>
        new(error);

    private static QuotaAdmissionTransactionDecision<CreateWebServiceOutcome> Abort(
        QuotaAdmissionTransactionContext<CreateWebServiceOutcome> transaction,
        CreateWebServiceError error) =>
        transaction.Abort(Error(error));

    private static ResolveTargetsOutcome PlacementError(
        NoDeploymentTargetAcceptsWorkloads error) =>
        new CreateWebServicePlacementError(error);

    private static ResolveTargetsOutcome PlacementError(
        NoDeploymentTargetAcceptsPublicWorkloads error) =>
        new CreateWebServicePlacementError(error);

    private static ResolveTargetsOutcome PlacementError(
        DeploymentTargetSelectionRequired error) =>
        new CreateWebServicePlacementError(error);

    private static ResolveTargetsOutcome PlacementError(
        CreateWebServiceTargetUnavailable error) =>
        new CreateWebServicePlacementError(error);

    private union ResolveTargetsOutcome(
    IReadOnlyList<ResolvedTargetRequest>,
    CreateWebServicePlacementError);

    private union CreateDeploymentRequestsOutcome(
    IReadOnlyList<CreateResourceDeploymentRequest>,
    CreateWebServiceError);

    private sealed record ResolvedTargetRequest(
    WebServiceDeploymentTargetRequest Request,
    ResolvedResourceDeploymentTarget Target);

    private sealed record PendingDeploymentRequest(
    ResolvedTargetRequest Target,
    string DeploymentId);

    private sealed record EligibleTarget(
    string DeploymentTargetId,
    long WorkloadConfigurationRevision,
    ResourceDeploymentIngressConfiguration? Ingress);
}

public sealed record CreateWebServiceRequest(
    [FromBody] CreateWebServiceRequestBody Body,
    [FromTenantHeader] string TenantId,
    [FromClaim(Claims.UserId)] ClaimValue<string> ActorUserId,
    [FromClaim(Claims.Email)] ClaimValue<string> ActorEmail)
    : IMediatorRequest<CreateWebServiceOutcome>, ITenantCreateOperation;

public sealed record CreateWebServiceRequestBody(
    string Name,
    string ResourceGroupId,
    WebServiceSpecDto Spec,
    WebServicePlacementRequest Placement,
    ImmutableHashSet<string> Tags,
    [property: JsonConverter(typeof(WebServiceExposureModeJsonConverter))]
    WebServiceExposureMode Exposure = WebServiceExposureMode.Private,
    [property: JsonConverter(typeof(JsonStringEnumConverter<WebServiceCreationMode>))]
    WebServiceCreationMode CreationMode = WebServiceCreationMode.Image);

public enum WebServiceCreationMode { Image, SourceBuild }

public sealed class WebServiceExposureModeJsonConverter
    : JsonStringEnumConverter<WebServiceExposureMode>
{
    public WebServiceExposureModeJsonConverter() : base(allowIntegerValues: false)
    {
    }
}

public sealed record WebServicePlacementRequest(
    string? IsolationScopeId,
    ImmutableList<WebServiceDeploymentTargetRequest> Targets);

public sealed record WebServiceDeploymentTargetRequest(
    string? DeploymentTargetId,
    int FixedReplicas = 1,
    string? ComputeProfileId = null);

public sealed record CreateWebServiceResponse(
    string WebServiceId,
    IReadOnlyList<PlacementSummaryDto> Deployments);

public sealed class CreateWebServiceRequestValidator : AbstractValidator<CreateWebServiceRequest>
{
    public CreateWebServiceRequestValidator()
    {
        RuleFor(request => request.ActorUserId.Value).NotEmpty();
        RuleFor(request => request.ActorEmail.Value).NotEmpty().EmailAddress();
        RuleFor(request => request.Body).SetValidator(new CreateWebServiceRequestBodyValidator());
    }
}

public sealed class CreateWebServiceRequestBodyValidator : AbstractValidator<CreateWebServiceRequestBody>
{
    public CreateWebServiceRequestBodyValidator()
    {
        RuleFor(body => body.Name).NotEmpty().MaximumLength(30);
        RuleFor(body => body.CreationMode).IsInEnum();
        RuleFor(body => body).Must(body => body.Spec is null ||
                (body.CreationMode == WebServiceCreationMode.SourceBuild) == (body.Spec.Image is null))
            .WithMessage("Provide an image for image mode; source build mode must omit the image.");
        RuleFor(body => body.ResourceGroupId).NotEmpty().MongoObjectId("Resource group ID");
        RuleFor(body => body.Spec).NotNull().SetValidator(new WebServiceSpecDtoValidator());
        RuleFor(body => body.Spec).Must(spec => spec?.Config is null ||
            (spec.Config.ConnectionEnvironmentReferences?.Count ?? 0) == 0 && (spec.Config.SecretEnvironmentReferences?.Count ?? 0) == 0)
            .WithMessage("Create the web service before authorizing dependency references.");
        RuleFor(body => body.Placement).NotNull().SetValidator(new WebServicePlacementRequestValidator());
        RuleFor(body => body.Exposure).IsInEnum();
        RuleFor(body => body)
            .Must(body => body.Exposure is not WebServiceExposureMode.Public ||
                          HasPublicIngressTarget(body.Spec?.Config?.PortMappings))
            .WithMessage("Public web services require a numeric TCP target port for their routed service port.");
    }

    internal static bool HasPublicIngressTarget(ImmutableList<PortMappingDto>? mappings)
    {
        var mapping = mappings?.FirstOrDefault();
        return mapping is not null &&
               WebServicePublicIngressPort.TryResolve(mapping.Protocol, mapping.TargetPort, out _);
    }
}

public sealed class WebServicePlacementRequestValidator : AbstractValidator<WebServicePlacementRequest>
{
    public WebServicePlacementRequestValidator()
    {
        RuleFor(placement => placement.IsolationScopeId)
            .OptionalMongoObjectId("Isolation scope ID");
        RuleFor(placement => placement.Targets)
            .NotNull()
            .Must(targets => targets.Count > 0)
            .WithMessage("At least one deployment target is required.");
        RuleForEach(placement => placement.Targets)
            .SetValidator(new WebServiceDeploymentTargetRequestValidator());
        RuleFor(placement => placement.Targets)
            .Must(HaveDistinctExplicitTargetIds)
            .WithMessage("Deployment target IDs must be unique.");
    }

    private static bool HaveDistinctExplicitTargetIds(
        ImmutableList<WebServiceDeploymentTargetRequest>? targets)
    {
        if (targets is null)
            return true;
        var ids = targets
            .Select(target => target.DeploymentTargetId?.Trim())
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Cast<string>()
            .ToArray();
        return ids.Distinct(StringComparer.Ordinal).Count() == ids.Length;
    }
}

public sealed class WebServiceDeploymentTargetRequestValidator
    : AbstractValidator<WebServiceDeploymentTargetRequest>
{
    public WebServiceDeploymentTargetRequestValidator()
    {
        RuleFor(target => target.DeploymentTargetId)
            .OptionalMongoObjectId("Deployment target ID");
        RuleFor(target => target.ComputeProfileId)
            .OptionalMongoObjectId("Compute profile ID");
        RuleFor(target => target.FixedReplicas).InclusiveBetween(1, 1000);
    }
}

public sealed class WebServiceSpecDtoValidator : AbstractValidator<WebServiceSpecDto>
{
    public WebServiceSpecDtoValidator()
    {
        When(spec => spec.Image is not null, () =>
        {
            RuleFor(spec => spec.Image!.Server).NotEmpty();
            RuleFor(spec => spec.Image!.Name).NotEmpty();
            RuleFor(spec => spec.Image!.Tag).NotEmpty();
        });
        RuleFor(spec => spec.Probes).NotNull();
        RuleFor(spec => spec.Config).NotNull();
        RuleFor(spec => spec.Config).Must(ValidDependencyReferences)
            .WithMessage("Use distinct environment names and valid logical connection or Secret references.");
    }

    private static bool ValidDependencyReferences(WebServiceConfigDto? config)
    {
        if (config is null) return true;
        var connections = config.ConnectionEnvironmentReferences;
        var secrets = config.SecretEnvironmentReferences;
        if (connections is null || secrets is null || config.EnvironmentVariables is null) return false;
        if (connections.Count + secrets.Count > 64) return false;
        var names = config.EnvironmentVariables.Keys.Concat(connections.Keys).Concat(secrets.Keys).ToArray();
        if (names.Distinct(StringComparer.Ordinal).Count() != names.Length || connections.Keys.Concat(secrets.Keys).Any(name =>
                name.Length > 128 || !System.Text.RegularExpressions.Regex.IsMatch(name, "^[A-Za-z_][A-Za-z0-9_]*$"))) return false;
        return connections.Values.All(reference => reference is not null && MongoDB.Bson.ObjectId.TryParse(reference.ConnectionId, out _) &&
                   (reference.ValueKind is "Host" or "Port" && reference.ConfigurationKey is null || reference.ValueKind == "Configuration" &&
                       !string.IsNullOrWhiteSpace(reference.ConfigurationKey) && reference.ConfigurationKey.Length <= 128 && !reference.ConfigurationKey.Any(char.IsControl))) &&
               secrets.Values.All(reference => reference is not null && MongoDB.Bson.ObjectId.TryParse(reference.SecretId, out _) &&
                   reference.Key is { Length: > 0 and <= 253 } && System.Text.RegularExpressions.Regex.IsMatch(reference.Key, "^[A-Za-z0-9._-]+$"));
    }
}
