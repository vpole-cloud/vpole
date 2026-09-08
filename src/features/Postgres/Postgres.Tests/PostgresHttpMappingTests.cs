using System.Net;
using Pole.ASP.Helpers;
using Pole.Features.Postgres.Application.Common;
using Pole.Features.Postgres.Application.Endpoints;
using Pole.Features.ComputeProfiles.Contracts;
using Pole.Features.ComputeProfiles.Contracts.HttpMapping;
using Xunit;
namespace Pole.Features.Postgres.Tests;

public sealed class PostgresHttpMappingTests
{
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
}
