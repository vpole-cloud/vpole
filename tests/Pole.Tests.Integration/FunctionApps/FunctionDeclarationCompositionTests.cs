// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

using Microsoft.Extensions.DependencyInjection;
using Pole.Features.Functions.Application.Common;
using Pole.Features.GitOps.Contracts;
using Pole.Tests.Integration.Infrastructure;
using Xunit;

namespace Pole.Tests.Integration.FunctionApps;

[Collection(IntegrationTestCollection.Tenant2)]
public sealed class FunctionDeclarationCompositionTests(
    MongoReplicaSetFixture fixture
) : MediatorIntegrationTestBase(fixture)
{
    [Fact]
    public void Feature_registration_preserves_capability_discovery_on_one_scoped_provider()
    {
        var provider = Services.GetServices<IDeclarativeResourceProvider>().Single(item => item.Kind == "FunctionApp");
        Assert.IsAssignableFrom<ITransactionalDeclarativeResourceProvider>(provider);
        Assert.IsAssignableFrom<IDeclarativeReferencePreparation>(provider);
        Assert.Equal(
            FunctionResourceDeployment.ResourceType,
            Assert.IsAssignableFrom<IDeclarativeRegisteredResourceProvider>(provider).ResourceType);
        Assert.Same(
            provider,
            Services.GetServices<IDeclarativeResourceLifecycleProvider>().Single(item => item.Kind == "FunctionApp"));
        Assert.Same(
            provider,
            Services.GetServices<IDeclarativeLifecycleDependencyProvider>()
                .Single(item => item.DependencyOwner == "Functions"));
        Assert.Same(
            provider,
            Services.GetServices<IDeclarativePreviewReferenceProvider>()
                .Single(item => item is IDeclarativeResourceProvider { Kind: "FunctionApp" }));
        Assert.Same(
            provider,
            Services.GetServices<IDeclarativeAdoptionPreviewProvider>()
                .Single(item => item is IDeclarativeResourceProvider { Kind: "FunctionApp" }));
    }
}
