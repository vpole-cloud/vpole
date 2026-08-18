using System.Net;
using Pole.ASP.Helpers;
using Pole.Features.DeploymentTargets.Contracts;
using Pole.Features.DeploymentTargets.Contracts.HttpMapping;
using Pole.Features.Domains.Application.Endpoints;
using Xunit;

namespace Pole.Tests.Integration.Api;

public sealed class HttpProblemMapperTests
{
    [Fact]
    public void ContractUnionMappingProvidesTheDefaultProblem()
    {
        var mapper = CreateMapper();
        DeploymentTargetDomainReservationAdmissionError error =
            new DeploymentTargetNotFound("target-1");

        var problem = mapper.Map(error);

        Assert.Equal((int)HttpStatusCode.NotFound, problem.StatusCode);
        Assert.Equal(
            "https://pole.dev/problems/deployment-target-not-found",
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
            "https://pole.dev/problems/deployment-target-disabled",
            problem.Type);
        Assert.Equal("deployment-targets.disabled", problem.Code);
        Assert.Equal("target-2", problem.Extensions["deploymentTargetId"]);
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
                    "https://pole.dev/problems/domain-zone-target-unavailable",
                    "Domain zone target is unavailable",
                    "domains.zone.target-unavailable",
                    extensions: new Dictionary<string, object?>
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
            DeploymentTargetContractHttpMappings.All.Single();

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

    private static HttpProblemMapper CreateMapper() =>
        new(DeploymentTargetContractHttpMappings.All);
}
