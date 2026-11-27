// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

using Microsoft.Extensions.DependencyInjection;
using Pole.Database;
using Pole.Features.Domains.Application;
using Pole.Features.Domains.Migrations;
using Pole.Migrations;
using Pole.Platform.Hosting;
using Pole.Server.Extensions;
using Xunit;

namespace Pole.Tests.Integration.Database;

public sealed class MigrationRegistrationTests
{
    [Fact]
    public void Production_composition_discovers_all_migration_projects_without_runtime_registration()
    {
        var services = new ServiceCollection();
        services.AddMigrations(PolePlatform.MigrationAssemblies).AddServiceMigrations();
        using var provider = services.BuildServiceProvider(validateScopes: true);
        var migrations = provider.GetServices<IMigration>().ToArray();

        var featureAssemblies = migrations.Select(migration => migration.GetType().Assembly.GetName().Name!)
            .Where(name => name != typeof(FeatureScopedMigration).Assembly.GetName().Name!)
            .ToHashSet();
        Assert.Equal(
            new[]
                {
                    "Backups", "ComputeProfiles", "ContainerRegistries", "DeploymentTargets", "Domains", "Functions",
                    "GitOps",
                    "GitProviders", "Outbox", "PrivateAccess", "Quotas", "ResourceConnections", "ResourceDeployments",
                    "ResourceGroups", "Search", "Secrets", "SourceBuilds", "StorageProfiles", "Tenants", "Users",
                    "WebServices",
                }.Select(feature => $"Pole.Features.{feature}.Migrations")
                .Order(StringComparer.Ordinal),
            featureAssemblies.Order(StringComparer.Ordinal));
        Assert.All(
            PolePlatform.MigrationAssemblies.Concat(ServiceMigrations.ApplicationMigrationAssemblies),
            assembly => Assert.EndsWith(".Migrations", assembly.GetName().Name!));
        Assert.DoesNotContain(typeof(DomainsFeatureRegistration).Assembly, PolePlatform.MigrationAssemblies);

        var scoped = migrations.OfType<FeatureScopedMigration>().ToArray();
        Assert.Equal(
            new[]
            {
                "pole.postgres/6a700003b213917a38c38001",
                "pole.redis/6a700001b213917a38c38001",
                "pole.redis/6a700002b213917a38c38001",
            },
            scoped.Select(migration => migration.DurableId).Order(StringComparer.Ordinal));
        Assert.Equal(
            migrations.Length,
            migrations.Select(migration =>
                    migration is FeatureScopedMigration scope ? scope.DurableId : migration.Id.ToString())
                .Distinct(StringComparer.Ordinal)
                .Count());
    }

    [Fact]
    public void Repeated_assembly_inputs_do_not_register_duplicate_migrations()
    {
        var assembly = typeof(DomainIndexesMigration).Assembly;
        var services = new ServiceCollection().AddMigrations(assembly, assembly);
        using var provider = services.BuildServiceProvider();
        Assert.Equal(3, provider.GetServices<IMigration>().Count());
    }
}
