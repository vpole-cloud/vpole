using MongoDB.Driver;
using Pole.Features.ResourceDeployments.Contracts;
using Pole.ResultType;

namespace Pole.Features.ResourceDeployments.Application.Persistence;

internal sealed class MongoCreateResourceDeployment(
    CreateResourceDeploymentSet createResourceDeploymentSet)
{
    public async Task<Result<PlacementSummaryDto, CreateResourceDeploymentError>> InvokeAsync(
        CreateResourceDeploymentRequest request,
        IClientSessionHandle session,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var result = await createResourceDeploymentSet(
            new CreateResourceDeploymentSetRequest([request]),
            session,
            cancellationToken);
        return result.TryGetValue(out var deployments, out var error)
            ? new Ok<PlacementSummaryDto>(deployments.Single())
            : new Err<CreateResourceDeploymentError>(error);
    }
}
