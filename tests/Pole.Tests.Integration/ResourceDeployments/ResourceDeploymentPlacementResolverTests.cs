using Pole.Features.DeploymentTargets.Contracts;
using Pole.Features.ResourceDeployments.Application.Placement;
using Pole.Features.ResourceDeployments.Contracts;
using Pole.ResultType;
using Xunit;

namespace Pole.Tests.Integration.ResourceDeployments;

public sealed class ResourceDeploymentPlacementResolverTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ExplicitTargetAdmission_PreservesLegacyFailureSemantics(
        bool isNotFound)
    {
        const string targetId = "507f1f77bcf86cd799439011";
        DeploymentTargetWorkloadAdmissionError admissionError = isNotFound
            ? new DeploymentTargetWorkloadAdmissionError(
                new DeploymentTargetNotFound(targetId))
            : new DeploymentTargetWorkloadAdmissionError(
                new InvalidDeploymentTargetId(targetId));
        var resolver = new ResourceDeploymentPlacementResolver(
            (_, _) => Task.FromResult<Result<
                DeploymentTargetLookupDto,
                DeploymentTargetWorkloadAdmissionError>>(
                new Err<DeploymentTargetWorkloadAdmissionError>(
                    admissionError)),
            _ => Task.FromResult<IReadOnlyList<DeploymentTargetLookupDto>>([]));

        var result = await resolver.InvokeAsync(targetId, requiresIngress: false);

        var failure = Assert.IsType<Failure<ResolvedResourceDeploymentTarget>>(result).Error;
        if (isNotFound)
        {
            var notFound = Assert.IsType<NotFoundFailure>(failure);
            Assert.Equal(targetId, notFound.Id);
        }
        else
        {
            var validation = Assert.IsType<ValidationFailure>(failure);
            Assert.Contains("DeploymentTargetId", validation.ValidationErrors.Keys);
        }
    }
}
