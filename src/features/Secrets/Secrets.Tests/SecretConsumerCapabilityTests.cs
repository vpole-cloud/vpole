// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

using Pole.Features.Secrets.Application.Bindings;
using Pole.Features.Secrets.Contracts;
using Pole.Features.Secrets.Domain;
using Pole.Features.Secrets.Primitives;
using Pole.Workloads;
using Xunit;

namespace Pole.Features.Secrets.Tests;

public sealed class SecretConsumerCapabilityTests
{
    [Theory]
    [InlineData("sample/archive-a", "ArchiveA")]
    [InlineData("another/consumer-b", "ConsumerB")]
    public void Purpose_bound_projection_uses_contributed_capabilities_instead_of_resource_identity(
        string resourceType,
        string kind)
    {
        var type = ResourceTypeId.Create(resourceType);
        var catalog = new SecretBindingConsumerCatalog(
        [
            new SecretDeclarationConsumer(
                kind,
                type,
                SecretBindingUsage.BackupStorage,
                SecretConsumerProjectionPolicy.DeclaredPurposeForAllDeployments,
                SecretConsumerPlacementRequirement.AllowBeforePlacement,
                SecretConsumerValueAccess.GrantedKeys),
        ]);
        var permitted = SecretBinding.Create(
            "binding",
            resourceType,
            "resource",
            new AllSecretDeployments(),
            [],
            SecretBindingUsage.BackupStorage);
        Assert.True(catalog.GrantsProjection(permitted));
        Assert.True(catalog.AllowsBeforePlacement(type));
        Assert.False(catalog.GrantsProjection(permitted with { Usage = SecretBindingUsage.Runtime }));
        Assert.False(
            catalog.GrantsProjection(
                permitted with
                {
                    Selection = new SelectedSecretDeployments(["one-deployment"]),
                }));
    }

    [Fact]
    public void Invalid_contribution_policy_is_rejected_before_any_secret_access()
    {
        var contribution = new SecretDeclarationConsumer(
            "Database",
            ResourceTypeId.Create("sample/database"),
            SecretBindingUsage.Bootstrap,
            SecretConsumerProjectionPolicy.RuntimeBindings,
            SecretConsumerPlacementRequirement.LiveDeployments,
            (SecretConsumerValueAccess)99);
        Assert.Throws<InvalidOperationException>(() => new SecretBindingConsumerCatalog([contribution]));
    }

    [Fact]
    public void Bootstrap_binding_acceptance_does_not_grant_runtime_projection()
    {
        var type = ResourceTypeId.Create("sample/database");
        var catalog = new SecretBindingConsumerCatalog(
        [
            new SecretDeclarationConsumer(
                "Database",
                type,
                SecretBindingUsage.Bootstrap,
                SecretConsumerProjectionPolicy.RuntimeBindings,
                SecretConsumerPlacementRequirement.LiveDeployments,
                SecretConsumerValueAccess.ProjectionOnly),
        ]);
        var binding = SecretBinding.Create(
            "binding",
            type.Value,
            "resource",
            new AllSecretDeployments(),
            [],
            SecretBindingUsage.Bootstrap);
        Assert.True(catalog.AcceptsBindingPurpose(binding));
        Assert.False(catalog.GrantsProjection(binding));
    }

    [Fact]
    public void Unknown_runtime_consumers_keep_projection_only_semantics()
    {
        var catalog = new SecretBindingConsumerCatalog([]);
        var type = ResourceTypeId.Create("sample/workload");
        Assert.Null(catalog.Find(type));
        Assert.False(catalog.AllowsBeforePlacement(type));
        Assert.True(
            catalog.GrantsProjection(
                SecretBinding.Create("binding", type.Value, "resource", new AllSecretDeployments(), [])));
        Assert.False(
            catalog.GrantsProjection(
                SecretBinding.Create(
                    "binding",
                    type.Value,
                    "resource",
                    new AllSecretDeployments(),
                    [],
                    SecretBindingUsage.BackupStorage)));
    }
}
