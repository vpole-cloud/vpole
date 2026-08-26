using MongoDB.Driver;
using Pole.Features.ResourceGroups.Domain;
using Pole.Features.Shared.Models;

namespace Pole.Features.ResourceGroups.Application.Persistence.ResourceGroups;

public delegate Task RegisterResourceGroupWebService(
    string tenantId,
    string resourceGroupId,
    string webServiceId,
    string webServiceName,
    CancellationToken cancellationToken = default);

internal sealed class MongoRegisterResourceGroupWebService(
    IMongoCollection<ResourceGroup> resourceGroups)
{
    public Task InvokeAsync(
        string tenantId,
        string resourceGroupId,
        string webServiceId,
        string webServiceName,
        CancellationToken cancellationToken = default) =>
        resourceGroups.UpdateOneAsync(
            ResourceGroupFilters.WithoutResource(
                tenantId,
                resourceGroupId,
                webServiceId),
            Builders<ResourceGroup>.Update.Push(
                group => group.Resources,
                new ShortResourceInfo(webServiceId, webServiceName, ResourceType.WebService)),
            cancellationToken: cancellationToken);
}
