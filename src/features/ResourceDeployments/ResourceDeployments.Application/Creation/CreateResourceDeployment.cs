// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

using MongoDB.Driver;
using Pole.Features.ResourceDeployments.Contracts;

namespace Pole.Features.ResourceDeployments.Application.Creation;

internal sealed class MongoCreateResourceDeployment(
    CreateResourceDeploymentSet createResourceDeploymentSet
)
{
    public async Task<CreateResourceDeploymentOutcome> InvokeAsync(
        CreateResourceDeploymentRequest request,
        IClientSessionHandle session,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var result = await createResourceDeploymentSet(
            new CreateResourceDeploymentSetRequest([request]),
            session,
            cancellationToken);
        return result switch
        {
            CreatedResourceDeployments created => created.Placements.Single(),
            CreateResourceDeploymentError error => error,
        };
    }
}
