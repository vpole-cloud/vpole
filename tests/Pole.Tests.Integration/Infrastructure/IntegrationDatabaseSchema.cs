using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Driver;
using Pole.Database;
using Pole.Features.ComputeProfiles.Domain;
using Pole.Features.ComputeProfiles.Migrations;
using Pole.Features.DeploymentTargets.Migrations;
using Pole.Features.Outbox.Migrations;
using Pole.Features.ResourceDeployments.Migrations;
using Pole.Features.StorageProfiles.Migrations;
using Pole.Platform.Hosting;
using Pole.Testing;

namespace Pole.Tests.Integration.Infrastructure;

internal static class IntegrationDatabaseSchema
{
    public static async Task PrepareAsync(TestDatabaseScope scope)
    {
        using var timing = new TestTimings(typeof(IntegrationDatabaseSchema), fixture: true)
            .Measure("fixture.database.migrations." + scope.SchemaProfile);
        var services = new ServiceCollection().AddLogging().AddSingleton(scope.Database);
        services.AddSingleton<MigrationRunner>();
        services.AddMigrations(MigrationAssemblies(scope.SchemaProfile));
        if (scope.SchemaProfile == IntegrationSchemaProfile.Full)
        {
            services.AddFeatureMigrations("pole.postgres",
                typeof(Pole.Features.Postgres.Migrations.PostgresNameIndexesMigration).Assembly);
            services.AddFeatureMigrations("pole.redis",
                typeof(Pole.Features.RedisServices.Migrations.RedisNameIndexesMigration).Assembly);
        }
        await using var provider = services.BuildServiceProvider();
        await provider.GetRequiredService<MigrationRunner>().RunMigrations();
        if (scope.SchemaProfile is IntegrationSchemaProfile.Full or IntegrationSchemaProfile.Catalogs or
            IntegrationSchemaProfile.StorageCatalog)
            await scope.DbContext.StorageProfiles.InsertOneAsync(TestData.SeedStorageProfile());
        if (scope.SchemaProfile is IntegrationSchemaProfile.Full or IntegrationSchemaProfile.Catalogs or
            IntegrationSchemaProfile.ComputeCatalog)
            await scope.Database.GetCollection<ComputeProfile>("ComputeProfiles")
                .InsertOneAsync(TestData.SeedComputeProfile());
    }

    // Select complete owner assemblies so schema, history and indexes retain production ordering.
    private static Assembly[] MigrationAssemblies(IntegrationSchemaProfile profile) => profile switch
    {
        IntegrationSchemaProfile.Full =>
        [
            .. PolePlatform.MigrationAssemblies,
            typeof(Pole.Features.WebServices.Migrations.WebServiceSchemaValidationMigration).Assembly,
            typeof(Pole.Features.Functions.Migrations.FunctionAppsInitialMigration).Assembly,
            typeof(Pole.Features.ResourceConnections.Migrations.ResourceConnectionIndexesMigration).Assembly
        ],
        IntegrationSchemaProfile.Identity => [typeof(OutboxMessageMigration).Assembly],
        IntegrationSchemaProfile.DeploymentTargets =>
        [
            typeof(DeploymentTargetMigration).Assembly,
            typeof(OutboxMessageMigration).Assembly
        ],
        IntegrationSchemaProfile.StorageCatalog =>
        [
            typeof(StorageProfileMigration).Assembly,
            typeof(OutboxMessageMigration).Assembly
        ],
        IntegrationSchemaProfile.StorageObservation =>
        [
            typeof(StorageProfileMigration).Assembly,
            typeof(DeploymentTargetMigration).Assembly,
            typeof(OutboxMessageMigration).Assembly
        ],
        IntegrationSchemaProfile.ComputeCatalog =>
        [
            typeof(ComputeProfileSchemaValidationMigration).Assembly,
            typeof(DeploymentTargetMigration).Assembly,
            typeof(ResourceDeploymentMigration).Assembly,
            typeof(OutboxMessageMigration).Assembly
        ],
        IntegrationSchemaProfile.Catalogs =>
        [
            typeof(ComputeProfileSchemaValidationMigration).Assembly,
            typeof(StorageProfileMigration).Assembly,
            typeof(DeploymentTargetMigration).Assembly,
            typeof(ResourceDeploymentMigration).Assembly,
            typeof(OutboxMessageMigration).Assembly
        ],
        _ => throw new ArgumentOutOfRangeException(nameof(profile))
    };
}
