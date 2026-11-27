// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Driver;
using Pole.Features.ResourceDeployments.Application;
using Pole.Features.ResourceDeployments.Domain.Observation;
using Pole.Features.StorageProfiles.Application;
using Pole.Features.StorageProfiles.Domain.Observation;
using Xunit;

namespace Pole.Tests.Integration.Infrastructure;

public sealed class ObservationCollectionRegistrationTests
{
    [Fact]
    public void Feature_composition_registers_shared_observation_collections_by_owner_name()
    {
        var client = new MongoClient("mongodb://localhost");
        var services = new ServiceCollection();
        services.AddSingleton<IMongoClient>(client);
        services.AddSingleton<IMongoDatabase>(client.GetDatabase("pole-test"));
        services.AddResourceDeploymentsFeature(new ConfigurationBuilder().Build());
        services.AddStorageProfilesFeature();

        using var provider = services.BuildServiceProvider();
        Assert.Equal(
            new[]
            {
                "ResourceDeploymentObservationControls",
                "ResourceDeploymentObservationDemands",
                "ResourceDeploymentObservationSnapshots",
                "StorageObservationControls",
                "StorageObservationDemands",
                "StorageObservationSnapshots",
            },
            new[]
            {
                provider.GetRequiredService<IMongoCollection<ObservationControlDocument>>()
                    .CollectionNamespace.CollectionName,
                provider.GetRequiredService<IMongoCollection<ObservationDemandDocument>>()
                    .CollectionNamespace.CollectionName,
                provider.GetRequiredService<IMongoCollection<ObservationSnapshotDocument>>()
                    .CollectionNamespace.CollectionName,
                provider.GetRequiredService<IMongoCollection<StorageObservationControlDocument>>()
                    .CollectionNamespace.CollectionName,
                provider.GetRequiredService<IMongoCollection<StorageObservationDemandDocument>>()
                    .CollectionNamespace.CollectionName,
                provider.GetRequiredService<IMongoCollection<StorageObservationSnapshotDocument>>()
                    .CollectionNamespace.CollectionName,
            });
    }
}
