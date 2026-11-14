using Pole.Features.Backups.Migrations;
using System.Reflection;
using Pole.Features.Functions.Migrations;
using Pole.Features.Postgres.Migrations;
using Pole.Features.RedisServices.Migrations;
using Pole.Features.ResourceConnections.Migrations;
using Pole.Features.WebServices.Migrations;
using Pole.Platform.Hosting;

namespace Pole.Server.Extensions;

/// <summary>Migration composition for the statically shipped workload providers.</summary>
public static class ServiceMigrations
{
    public static Assembly[] ApplicationMigrationAssemblies =>
    [
        typeof(WebServiceSchemaValidationMigration).Assembly,
        typeof(FunctionAppsInitialMigration).Assembly,
        typeof(ResourceConnectionIndexesMigration).Assembly,
        typeof(BackupsIndexesMigration).Assembly,
    ];

    public static IServiceCollection AddServiceMigrations(this IServiceCollection services) => services
        .AddMigrations(ApplicationMigrationAssemblies)
        .AddFeatureMigrations("pole.redis", typeof(RedisSchemaValidationMigration).Assembly)
        .AddFeatureMigrations("pole.postgres", typeof(PostgresNameIndexesMigration).Assembly);
}
