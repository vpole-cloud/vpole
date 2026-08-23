using Pole.Features.DeploymentTargets.Contracts;
using Pole.Features.ResourceDeployments.Application.Placement;
using Pole.Features.ResourceDeployments.Contracts;
using Pole.ResultType;
using Xunit;

namespace Pole.Tests.Integration.ResourceDeployments;

public sealed class ResourceDeploymentPlacementResolverTests
{
    [Fact]
    public async Task ExplicitTargetAdmission_MapsNotFound()
    {
        const string targetId = "507f1f77bcf86cd799439011";
        DeploymentTargetWorkloadAdmissionError admissionError = new(
            new DeploymentTargetNotFound(targetId));
        var resolver = new ResourceDeploymentPlacementResolver(
            (_, _) => Task.FromResult<Result<
                DeploymentTargetLookupDto,
                DeploymentTargetWorkloadAdmissionError>>(
                new Err<DeploymentTargetWorkloadAdmissionError>(
                    admissionError)),
            _ => Task.FromResult<IReadOnlyList<DeploymentTargetLookupDto>>([]));

        var result = await resolver.InvokeAsync(targetId, requiresIngress: false);

        var failure = Assert.IsType<Failure<ResolvedResourceDeploymentTarget>>(result).Error;
        var notFound = Assert.IsType<NotFoundFailure>(failure);
        Assert.Equal(targetId, notFound.Id);
    }
}
