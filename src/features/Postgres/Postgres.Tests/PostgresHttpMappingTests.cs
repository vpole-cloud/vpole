using System.Net;
using Pole.Features.ComputeProfiles.Contracts;
using Pole.Features.Postgres.Application.Common;
using Pole.Features.Postgres.Application.Creation;
using Pole.Features.Postgres.Application.Endpoints;
using Xunit;

namespace Pole.Features.Postgres.Tests;

public sealed class PostgresHttpMappingTests
{
    [Fact]
    public void PostgresOutcomeMapsProviderOwnedComputeError()
    {
        var error = UpdatePostgresClusterError.NewUpdateCompute(
            new ComputeProfileResolutionError(new ComputeProfileNotFound("compute-1")));

        var problem = PostgresHttpMappings.mapUpdateError(error);

        Assert.Equal((int)HttpStatusCode.NotFound, problem.StatusCode);
        Assert.Equal("compute-profiles.not-found", problem.Code);
        Assert.Equal("compute-1", problem.Extensions["computeProfileId"]);
    }

    [Fact]
    public void PostgresOutcomeUsesThePostgresOwnedStateMapping()
    {
        var error = CreatePostgresClusterStateError.NewPostgresClusterComputeRuntimeClassUnsupported("target-1");

        var problem = PostgresHttpMappings.mapCreateError(CreatePostgresClusterError.NewCreateState(error));

        Assert.Equal((int)HttpStatusCode.UnprocessableEntity, problem.StatusCode);
        Assert.Equal("postgres.compute.runtime-class-unsupported", problem.Code);
        Assert.Equal("target-1", problem.Extensions["deploymentTargetId"]);
    }
}
