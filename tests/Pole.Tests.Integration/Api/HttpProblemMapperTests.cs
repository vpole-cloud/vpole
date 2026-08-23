using System.Net;
using Pole.ASP.Helpers;
using Pole.Features.ComputeProfiles.Application.Common;
using Pole.Features.ComputeProfiles.Application.Endpoints;
using Pole.Features.ComputeProfiles.Application.Persistence;
using Pole.Features.ComputeProfiles.Contracts;
using Pole.Features.DeploymentTargets.Contracts;
using Pole.Features.DeploymentTargets.Contracts.HttpMapping;
using Pole.Features.Domains.Application.Common;
using Pole.Features.Domains.Application.Endpoints;
using Pole.Features.Domains.Application.Persistence;
using Pole.Features.Quotas.Application.Common;
using Pole.Features.Quotas.Application.Endpoints;
using Pole.Features.Quotas.Contracts;
using Pole.Features.Quotas.Contracts.HttpMapping;
using Pole.Features.Quotas.Primitives;
using Pole.Features.Postgres.Application.Common;
using Pole.Features.Postgres.Application.Endpoints;
using Pole.Features.ResourceDeployments.Application.Common;
using Pole.Features.ResourceDeployments.Application.Endpoints;
using Pole.Features.ResourceDeployments.Contracts;
using Pole.Features.ResourceDeployments.Contracts.HttpMapping;
using Pole.Features.ResourceGroups.Application.Common;
using Pole.Features.ResourceGroups.Contracts;
using Pole.Features.RedisServices.Application.Common;
using Pole.Features.RedisServices.Application.Endpoints;
using Pole.Features.StorageProfiles.Application.Common;
using Pole.Features.StorageProfiles.Contracts;
using Pole.Features.WebServices.Application.Common;
using Pole.Features.WebServices.Application.Endpoints;
using Pole.Features.Tenants;
using Pole.Mediator;
using Xunit;

namespace Pole.Tests.Integration.Api;

public sealed class HttpProblemMapperTests
{
    [Fact]
    public void RedisOutcomeUnwrapsProviderOwnedStorageError()
    {
        var mapper = new HttpProblemMapper(
        [
            .. RedisHttpMappings.All,
            .. StorageProfileContractHttpMappings.All
        ]);
        CreateRedisServiceError error = new(
            new StorageProfileResolutionError(
                new StorageProfileNotFound("storage-1")));

        var problem = mapper.Map(error);

        Assert.Equal((int)HttpStatusCode.NotFound, problem.StatusCode);
        Assert.Equal("storage-profiles.not-found", problem.Code);
        Assert.Equal("storage-1", problem.Extensions["storageProfileId"]);
    }

    [Fact]
    public void RedisOutcomeUsesTheRedisOwnedStateMapping()
    {
        var mapper = new HttpProblemMapper(RedisHttpMappings.All);
        UpdateRedisServiceError error = new(
            new RedisUpdateStateError(
                new RedisStorageProfileChangeNotAllowed("redis-1")));

        var problem = mapper.Map(error);

        Assert.Equal((int)HttpStatusCode.UnprocessableEntity, problem.StatusCode);
        Assert.Equal("redis-services.storage-profile-change-not-allowed", problem.Code);
        Assert.Equal("redis-1", problem.Extensions["redisId"]);
    }

    [Fact]
    public void NewWorkloadAdmissionMappingIncludesSelectionDetails()
    {
        var mapper = new HttpProblemMapper(DeploymentTargetContractHttpMappings.All);
        DeploymentTargetNewWorkloadAdmissionError error = new(
            new DeploymentTargetWorkloadSelectionRequired(["target-1", "target-2"]));

        var problem = mapper.Map(error);

        Assert.Equal((int)HttpStatusCode.UnprocessableEntity, problem.StatusCode);
        Assert.Equal("deployment-targets.selection-required", problem.Code);
        Assert.Equal(
            ["target-1", "target-2"],
            Assert.IsAssignableFrom<IReadOnlyList<string>>(
                problem.Extensions["eligibleDeploymentTargetIds"]));
    }

    [Fact]
    public void NewWorkloadAdmissionMappingDistinguishesMissingCapabilities()
    {
        var mapper = new HttpProblemMapper(DeploymentTargetContractHttpMappings.All);
        DeploymentTargetNewWorkloadAdmissionError error = new(
            new DeploymentTargetRequiredCapabilitiesUnavailable(
                "target-1",
                DeploymentTargetRequiredCapabilities.PublicIngress));

        var problem = mapper.Map(error);

        Assert.Equal((int)HttpStatusCode.UnprocessableEntity, problem.StatusCode);
        Assert.Equal("deployment-targets.required-capabilities-unavailable", problem.Code);
        Assert.Equal("target-1", problem.Extensions["deploymentTargetId"]);
        Assert.Equal("PublicIngress", problem.Extensions["requiredCapabilities"]);
    }

    [Fact]
    public void DefaultDeploymentTargetAdmissionUsesTheSharedContractMapping()
    {
        var mapper = new HttpProblemMapper(DeploymentTargetContractHttpMappings.All);
        DefaultDeploymentTargetWorkloadAdmissionError error = new(
            new DefaultDeploymentTargetNotConfigured());

        var problem = mapper.Map(error);

        Assert.Equal((int)HttpStatusCode.Conflict, problem.StatusCode);
        Assert.Equal("deployment-targets.default-not-configured", problem.Code);
    }

    [Fact]
    public void DefaultDeploymentTargetLookupMapsMissingConfiguration()
    {
        var mapper = new HttpProblemMapper(DeploymentTargetContractHttpMappings.All);
        DefaultDeploymentTargetLookupError error = new(
            new DefaultDeploymentTargetNotConfigured());

        var problem = mapper.Map(error);

        Assert.Equal((int)HttpStatusCode.Conflict, problem.StatusCode);
        Assert.Equal("deployment-targets.default-not-configured", problem.Code);
    }

    [Fact]
    public void WebServiceOutcomeUnwrapsProviderOwnedComputeError()
    {
        var mapper = new HttpProblemMapper(
        [
            .. WebServiceHttpMappings.All,
            .. ComputeProfileContractHttpMappings.All
        ]);
        AddWebServiceDeploymentError error = new(
            new ComputeProfileResolutionError(
                new ComputeProfileNotFound("compute-1")));

        var problem = mapper.Map(error);

        Assert.Equal((int)HttpStatusCode.NotFound, problem.StatusCode);
        Assert.Equal("compute-profiles.not-found", problem.Code);
        Assert.Equal("compute-1", problem.Extensions["computeProfileId"]);
    }

    [Fact]
    public void WebServiceOutcomeUnwrapsProviderOwnedResourceGroupError()
    {
        var mapper = new HttpProblemMapper(
        [
            .. WebServiceHttpMappings.All,
            .. ResourceGroupHttpMappings.All
        ]);
        CreateWebServiceError error = new(
            new ResourceGroupOwnershipError(
                new ResourceGroupOwnershipDeleting("resource-group-1")));

        var problem = mapper.Map(error);

        Assert.Equal((int)HttpStatusCode.Conflict, problem.StatusCode);
        Assert.Equal("resource-groups.deleting", problem.Code);
        Assert.Equal("resource-group-1", problem.Extensions["resourceGroupId"]);
    }

    [Fact]
    public void PostgresOutcomeUnwrapsProviderOwnedComputeError()
    {
        var mapper = new HttpProblemMapper(
        [
            .. PostgresHttpMappings.all,
            .. ComputeProfileContractHttpMappings.All
        ]);
        UpdatePostgresClusterError error = UpdatePostgresClusterError.NewUpdateCompute(
            new ComputeProfileResolutionError(
                new ComputeProfileNotFound("compute-1")));

        var problem = mapper.Map(error);

        Assert.Equal((int)HttpStatusCode.NotFound, problem.StatusCode);
        Assert.Equal("compute-profiles.not-found", problem.Code);
        Assert.Equal("compute-1", problem.Extensions["computeProfileId"]);
    }

    [Fact]
    public void PostgresOutcomeUsesThePostgresOwnedStateMapping()
    {
        var mapper = new HttpProblemMapper(PostgresHttpMappings.all);
        CreatePostgresClusterStateError error =
            CreatePostgresClusterStateError.NewPostgresClusterComputeRuntimeClassUnsupported(
                "target-1");

        var problem = mapper.Map(error);

        Assert.Equal((int)HttpStatusCode.UnprocessableEntity, problem.StatusCode);
        Assert.Equal("postgres.compute.runtime-class-unsupported", problem.Code);
        Assert.Equal("target-1", problem.Extensions["deploymentTargetId"]);
    }

    [Fact]
    public void DeleteComputeProfileOutcomeUsesTheComputeProfileApplicationMapping()
    {
        var mapper = new HttpProblemMapper(
        [
            .. ComputeProfileHttpMappings.All,
            .. ResourceDeploymentContractHttpMappings.All
        ]);
        DeleteComputeProfileError error =
            new(new ComputeProfileReferencedByResourceDeployment("compute-1"));
        DeleteComputeProfileOutcome outcome = new(error);

        var problem = mapper.Map(outcome);

        Assert.Equal((int)HttpStatusCode.Conflict, problem.StatusCode);
        Assert.Equal("resource-deployments.compute-profile-referenced", problem.Code);
        Assert.Equal("compute-1", problem.Extensions["computeProfileId"]);
    }

    [Fact]
    public void CreateComputeProfileOutcomeUnwrapsTargetReferenceProviderError()
    {
        var mapper = new HttpProblemMapper(
        [
            .. ComputeProfileHttpMappings.All,
            .. DeploymentTargetContractHttpMappings.All
        ]);
        CreateComputeProfileError error = new(
            new PersistCreatedComputeProfileError(
                new DeploymentTargetReferenceChangeRejected(
                    new DeploymentTargetReferenceOwner("compute-profile", "profile-1"),
                    ["target-1"])));

        var problem = mapper.Map(error);

        Assert.Equal((int)HttpStatusCode.Conflict, problem.StatusCode);
        Assert.Equal("deployment-targets.reference-change-rejected", problem.Code);
        Assert.Equal(
            ["target-1"],
            Assert.IsAssignableFrom<IReadOnlyList<string>>(
                problem.Extensions["deploymentTargetIds"]));
    }

    [Fact]
    public void ContractUnionMappingProvidesTheDefaultProblem()
    {
        var mapper = CreateMapper();
        DeploymentTargetDomainReservationAdmissionError error =
            new DeploymentTargetNotFound("target-1");

        var problem = mapper.Map(error);

        Assert.Equal((int)HttpStatusCode.NotFound, problem.StatusCode);
        Assert.Equal(
            "tag:pole.dev,2026:problem:deployment-targets.not-found",
            problem.Type);
        Assert.Equal("deployment-targets.not-found", problem.Code);
        Assert.Equal("target-1", problem.Extensions["deploymentTargetId"]);
    }

    [Fact]
    public void NestedConsumerUnionUsesTheProviderContractMapping()
    {
        var mapper = CreateMapper();
        DeploymentTargetDomainReservationAdmissionError contractError =
            new DeploymentTargetDisabled("target-2");
        CreateDomainZoneError consumerError = contractError;

        var problem = mapper.Map(consumerError);

        Assert.Equal((int)HttpStatusCode.Conflict, problem.StatusCode);
        Assert.Equal(
            "tag:pole.dev,2026:problem:deployment-targets.disabled",
            problem.Type);
        Assert.Equal("deployment-targets.disabled", problem.Code);
        Assert.Equal("target-2", problem.Extensions["deploymentTargetId"]);
    }

    [Fact]
    public void NestedConsumerUnionUsesTheApplicationMapping()
    {
        var mapper = CreateMapper();
        CreateManagedDomainZoneError applicationError =
            new ManagedDomainZoneAlreadyReserved("app.example.test");
        CreateDomainZoneError consumerError = applicationError;

        var problem = mapper.Map(consumerError);

        Assert.Equal((int)HttpStatusCode.Conflict, problem.StatusCode);
        Assert.Equal(
            "tag:pole.dev,2026:problem:domains.zone.already-reserved",
            problem.Type);
        Assert.Equal("domains.zone.already-reserved", problem.Code);
        Assert.Equal("app.example.test", problem.Extensions["domain"]);
    }

    [Fact]
    public void ApplicationUnionMappingPreservesStructuredExtensions()
    {
        var mapper = CreateMapper();
        DeleteManagedDomainZoneError error =
            new DomainZoneHasHostnameBindings("zone-1", 3);

        var problem = mapper.Map(error);

        Assert.Equal((int)HttpStatusCode.Conflict, problem.StatusCode);
        Assert.Equal("domains.zone.delete.has-hostname-bindings", problem.Code);
        Assert.Equal("zone-1", problem.Extensions["domainZoneId"]);
        Assert.Equal(3L, problem.Extensions["bindingCount"]);
    }

    [Fact]
    public void ConsumerLeafOverrideWinsOverTheProviderUnionDefault()
    {
        var mapper = CreateMapper();
        var overrides = new HttpProblemMappingSet(
        [
            HttpProblemMapping.For<DeploymentTargetNotFound>(notFound =>
                new HttpProblem(
                    (int)HttpStatusCode.UnprocessableEntity,
                    PoleProblemTypeId.FromCode("domains.zone.target-unavailable"),
                    "Domain zone target is unavailable",
                    "domains.zone.target-unavailable",
                    extensions: new Dictionary<string, object>
                    {
                        ["deploymentTargetId"] = notFound.DeploymentTargetId
                    }))
        ]);
        DeploymentTargetDomainReservationAdmissionError contractError =
            new DeploymentTargetNotFound("target-3");
        CreateDomainZoneError consumerError = contractError;

        var problem = mapper.Map(consumerError, overrides);

        Assert.Equal(
            (int)HttpStatusCode.UnprocessableEntity,
            problem.StatusCode);
        Assert.Equal(
            "domains.zone.target-unavailable",
            problem.Code);
        Assert.Equal("target-3", problem.Extensions["deploymentTargetId"]);
    }

    [Fact]
    public void DuplicateDefaultMappingsAreRejected()
    {
        var duplicate =
            DeploymentTargetContractHttpMappings.All.Single(mapping =>
                mapping.MappedType ==
                typeof(DeploymentTargetDomainReservationAdmissionError));

        var exception = Assert.Throws<InvalidOperationException>(() =>
            new HttpProblemMapper([duplicate, duplicate]));

        Assert.Contains(
            typeof(DeploymentTargetDomainReservationAdmissionError).FullName!,
            exception.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public void MissingMappingReportsTheResolvedUnionPath()
    {
        var mapper = new HttpProblemMapper([]);
        DeploymentTargetDomainReservationAdmissionError contractError =
            new DeploymentTargetNotFound("target-4");
        CreateDomainZoneError consumerError = contractError;

        var exception = Assert.Throws<InvalidOperationException>(() =>
            mapper.Map(consumerError));

        Assert.Contains(
            typeof(CreateDomainZoneError).FullName!,
            exception.Message,
            StringComparison.Ordinal);
        Assert.Contains(
            typeof(DeploymentTargetDomainReservationAdmissionError).FullName!,
            exception.Message,
            StringComparison.Ordinal);
        Assert.Contains(
            typeof(DeploymentTargetNotFound).FullName!,
            exception.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public void TenantPlatformAdminRejectionHasAStableProblemMapping()
    {
        var mapper = new HttpProblemMapper(
            TenantAuthorizationHttpProblemMappings.All);

        var problem = mapper.Map(new TenantPlatformAdminRequired());

        Assert.Equal((int)HttpStatusCode.Forbidden, problem.StatusCode);
        Assert.Equal(
            "tag:pole.dev,2026:problem:tenant.authorization.platform-admin-required",
            problem.Type);
        Assert.Equal(
            "tenant.authorization.platform-admin-required",
            problem.Code);
    }

    [Fact]
    public void TenantMemberAuthorizationRejectionsHaveStableProblemMappings()
    {
        var mapper = new HttpProblemMapper(
            TenantMemberAuthorizationHttpProblemMappings.All);
        (IMediatorRejection Rejection, HttpStatusCode Status, string Code)[] cases =
        [
            (
                new TenantAuthenticationRequired(),
                HttpStatusCode.Unauthorized,
                "tenant.authorization.authentication-required"),
            (
                new TenantUserIdentityRequired(),
                HttpStatusCode.Unauthorized,
                "tenant.authorization.user-identity-required"),
            (
                new TenantNotFound("tenant-1"),
                HttpStatusCode.NotFound,
                "tenants.not-found"),
            (
                new TenantMembershipRequired("tenant-1"),
                HttpStatusCode.Forbidden,
                "tenant.authorization.membership-required"),
            (
                new TenantMemberNotFound("tenant-1", "member-1"),
                HttpStatusCode.NotFound,
                "tenants.member.not-found")
        ];

        foreach (var testCase in cases)
        {
            var problem = mapper.Map(testCase.Rejection);

            Assert.Equal((int)testCase.Status, problem.StatusCode);
            Assert.Equal(testCase.Code, problem.Code);
            Assert.Equal(
                PoleProblemTypeId.FromCode(testCase.Code),
                problem.Type);
        }

        var memberNotFound = mapper.Map(
            new TenantMemberNotFound("tenant-1", "member-1"));
        Assert.Equal("tenant-1", memberNotFound.Extensions["tenantId"]);
        Assert.Equal("member-1", memberNotFound.Extensions["memberId"]);
    }

    [Fact]
    public void ResourceDeploymentRetryErrorsHaveStableProblemMappings()
    {
        var mapper = new HttpProblemMapper(
            ResourceDeploymentHttpMappings.All);
        (RetryResourceDeploymentError Error, HttpStatusCode Status, string Code)[]
            cases =
            [
                (
                    new ResourceDeploymentNotFound("deployment-1"),
                    HttpStatusCode.NotFound,
                    "resource-deployments.not-found"),
                (
                    new ResourceDeploymentNotRetryable("deployment-1"),
                    HttpStatusCode.Conflict,
                    "resource-deployments.retry.not-retryable"),
                (
                    new ResourceDeploymentRetryConflict("deployment-1"),
                    HttpStatusCode.Conflict,
                    "resource-deployments.retry.concurrent-change")
            ];

        foreach (var testCase in cases)
        {
            var problem = mapper.Map(testCase.Error);

            Assert.Equal((int)testCase.Status, problem.StatusCode);
            Assert.Equal(testCase.Code, problem.Code);
            Assert.Equal(
                PoleProblemTypeId.FromCode(testCase.Code),
                problem.Type);
            Assert.Equal(
                "deployment-1",
                problem.Extensions["deploymentId"]);
        }
    }

    [Fact]
    public void ResourceDeploymentContractErrorsHaveStableProblemMappings()
    {
        var mapper = new HttpProblemMapper(
            ResourceDeploymentContractHttpMappings.All);
        (object Error, HttpStatusCode Status, string Code)[] cases =
        [
            (
                new CreateResourceDeploymentError(
                    new ResourceDeploymentTargetConfigurationChanged(["target-1"])),
                HttpStatusCode.Conflict,
                "resource-deployments.target-changed"),
            (
                new CreateResourceDeploymentError(
                    new ResourceDeploymentComputeConfigurationChanged(["compute-1"])),
                HttpStatusCode.Conflict,
                "resource-deployments.compute-profile-changed"),
            (
                new CreateResourceDeploymentError(
                    new ResourceDeploymentStorageConfigurationChanged("storage-1")),
                HttpStatusCode.Conflict,
                "resource-deployments.storage-profile-changed"),
            (
                new CreateResourceDeploymentError(
                    new ResourcePlacementResourceGroupNotFound("group-1")),
                HttpStatusCode.NotFound,
                "resource-groups.not-found"),
            (
                new CreateResourceDeploymentError(
                    new ResourcePlacementUnavailable("group-1")),
                HttpStatusCode.Conflict,
                "resource-deployments.placement-unavailable"),
            (
                new CreateResourceDeploymentError(
                    new ResourcePlacementDeleting("target-1")),
                HttpStatusCode.Conflict,
                "resource-deployments.placement-deleting"),
            (
                new ResourceDeploymentMutationError(
                    new ResourceDeploymentMutationNotFound("resource-1")),
                HttpStatusCode.NotFound,
                "resource-deployments.not-found"),
            (
                new ResourceDeploymentMutationError(
                    new ResourceDeploymentChanged("resource-1")),
                HttpStatusCode.Conflict,
                "resource-deployments.changed"),
            (
                new ResourceDeploymentMutationError(
                    new ResourceDeploymentCardinalityConflict("resource-1")),
                HttpStatusCode.Conflict,
                "resource-deployments.cardinality-conflict"),
            (
                new ResourceDeploymentMutationError(
                    new ResourceDeploymentPendingDeletion("resource-1")),
                HttpStatusCode.Conflict,
                "resource-deployments.pending-deletion"),
            (
                new ResourceDeploymentMutationError(
                    new ResourceDeploymentPlacementUnavailable("resource-1")),
                HttpStatusCode.Conflict,
                "resource-deployments.placement-unavailable"),
            (
                new ResourceDeploymentMutationError(
                    new ResourceDeploymentReplacementTargetMismatch("resource-1")),
                HttpStatusCode.Conflict,
                "resource-deployments.replacement-target-mismatch"),
            (
                new ResourceDeploymentMutationError(
                    new ResourceDeploymentIngressConfigurationChanged(
                        "resource-1",
                        "deployment-1")),
                HttpStatusCode.Conflict,
                "resource-deployments.ingress-changed"),
            (
                new ResourceDeploymentMutationError(
                    new ResourceDeploymentRetryUnavailable(
                        "resource-1",
                        "deployment-1")),
                HttpStatusCode.Conflict,
                "resource-deployments.retry-unavailable"),
            (
                new ResourceDeploymentMutationError(
                    new ResourceDeploymentComputeConfigurationChanged(["compute-1"])),
                HttpStatusCode.Conflict,
                "resource-deployments.compute-profile-changed"),
            (
                new LoadCurrentResourceDeploymentComputeError(
                    new CurrentResourceDeploymentComputeNotFound("resource-1")),
                HttpStatusCode.NotFound,
                "resource-deployments.not-found"),
            (
                new LoadCurrentResourceDeploymentComputeError(
                    new CurrentResourceDeploymentComputeCardinalityConflict("resource-1")),
                HttpStatusCode.Conflict,
                "resource-deployments.compute-cardinality-conflict"),
            (
                new AdvanceResourceDeploymentRevisionError(
                    new ResourceDeploymentsNotFound("resource-1")),
                HttpStatusCode.NotFound,
                "resource-deployments.not-found"),
            (
                new AdvanceResourceDeploymentRevisionError(
                    new ResourceDeploymentsHaveNoActivePlacement("resource-1")),
                HttpStatusCode.Conflict,
                "resource-deployments.no-active-placement"),
            (
                new AdvanceResourceDeploymentRevisionError(
                    new ResourceDeploymentRevisionConflict("resource-1")),
                HttpStatusCode.Conflict,
                "resource-deployments.revision-conflict"),
            (
                new ResourceGroupHasUnfinishedDeployments("group-1"),
                HttpStatusCode.Conflict,
                "resource-deployments.resource-group-referenced"),
            (
                new IsolationScopeHasUnfinishedDeployments("group-1", "scope-1"),
                HttpStatusCode.Conflict,
                "resource-deployments.isolation-scope-referenced"),
            (
                new ComputeProfileReferencedByResourceDeployment("compute-1"),
                HttpStatusCode.Conflict,
                "resource-deployments.compute-profile-referenced"),
            (
                new ResourceDeploymentFinalizationConflict("resource-1"),
                HttpStatusCode.Conflict,
                "resource-deployments.finalization-conflict")
        ];

        foreach (var testCase in cases)
        {
            var problem = mapper.Map(testCase.Error);

            Assert.Equal((int)testCase.Status, problem.StatusCode);
            Assert.Equal(testCase.Code, problem.Code);
            Assert.Equal(
                PoleProblemTypeId.FromCode(testCase.Code),
                problem.Type);
        }
    }

    [Fact]
    public void QuotaReservationErrorsHaveStableProblemMappings()
    {
        var mapper = new HttpProblemMapper(
            QuotaContractHttpMappings.All);
        QuotaReservationError exceeded = new QuotaExceeded(
            new QuotaKey("CpuMillis"),
            Limit: 8_000,
            Requested: 2_000,
            Current: 7_000);
        (QuotaReservationError Error, HttpStatusCode Status, string Code)[]
            cases =
            [
                (
                    new InvalidQuotaReservationRequest(
                        new Dictionary<string, string[]>
                        {
                            ["TenantId"] = ["Tenant id is required."]
                        }),
                    HttpStatusCode.BadRequest,
                    "quotas.reservation.invalid"),
                (
                    exceeded,
                    HttpStatusCode.UnprocessableEntity,
                    "quotas.reservation.exceeded"),
                (
                    new QuotaReservationConflict(
                        "tenant-1",
                        QuotaResourceKey.Create("web-service", "resource-1")),
                    HttpStatusCode.Conflict,
                    "quotas.reservation.conflict")
            ];

        foreach (var testCase in cases)
        {
            var mapped = mapper.Map(testCase.Error);
            Assert.Equal((int)testCase.Status, mapped.StatusCode);
            Assert.Equal(testCase.Code, mapped.Code);
            Assert.Equal(
                PoleProblemTypeId.FromCode(testCase.Code),
                mapped.Type);
        }

        var problem = mapper.Map(exceeded);
        Assert.Equal("CpuMillis", problem.Extensions["quota"]);
        Assert.Equal(8_000L, problem.Extensions["limit"]);
        Assert.Equal(2_000L, problem.Extensions["requested"]);
        Assert.Equal(7_000L, problem.Extensions["current"]);
    }

    [Fact]
    public void QuotaExecutionErrorsUnwrapToStableProblemMappings()
    {
        var mapper = new HttpProblemMapper(
            QuotaContractHttpMappings.All);
        QuotaExecutionError error = new QuotaReservationCommitError(
            new QuotaReservationExpired("reservation-1"));

        var problem = mapper.Map(error);

        Assert.Equal((int)HttpStatusCode.Conflict, problem.StatusCode);
        Assert.Equal("quotas.reservation.commit-expired", problem.Code);
        Assert.Equal(
            PoleProblemTypeId.FromCode(problem.Code),
            problem.Type);
    }

    [Fact]
    public void InvalidQuotaPolicyHasFeatureLocalProblemMapping()
    {
        var mapper = new HttpProblemMapper(QuotaHttpMappings.All);
        var invalid = new InvalidTenantQuotaPolicy(
            new Dictionary<string, string[]>
            {
                ["Body.Limits[0].Key"] = ["Quota key is invalid."]
            });

        var problem = mapper.Map(invalid);

        Assert.Equal((int)HttpStatusCode.BadRequest, problem.StatusCode);
        Assert.Equal("quotas.policy.invalid", problem.Code);
        Assert.Same(invalid.Errors, problem.Extensions["errors"]);
    }

    [Fact]
    public void NullExtensionValueIsRejected()
    {
        var exception = Assert.Throws<ArgumentException>(() =>
            new HttpProblem(
                (int)HttpStatusCode.Conflict,
                PoleProblemTypeId.FromCode("example.problem"),
                "Example problem",
                "example.problem",
                extensions: new Dictionary<string, object>
                {
                    ["optional"] = null!
                }));

        Assert.Contains(
            "cannot be null",
            exception.Message,
            StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Domains.zone.not-found")]
    [InlineData("domains..not-found")]
    [InlineData("domains.zone.not_found")]
    public void PoleProblemTypeRejectsInvalidCodes(string code)
    {
        var exception = Assert.Throws<ArgumentException>(() =>
            PoleProblemTypeId.FromCode(code));

        Assert.Equal("code", exception.ParamName);
    }

    private static HttpProblemMapper CreateMapper() =>
        new(
        [
            .. DeploymentTargetContractHttpMappings.All,
            .. DomainHttpMappings.All
        ]);
}
