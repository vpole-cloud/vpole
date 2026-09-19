using Pole.Database;
using Pole.Platform.Hosting;
using Xunit;

namespace Pole.Tests.Skrynia;

public sealed class ProductionMigrationTests
{
    public static IEnumerable<object[]> Migrations => PolePlatform.ApplicationAssemblies.Concat([
            typeof(Pole.Features.WebServices.Application.Common.WebServiceEndpoints).Assembly,
            typeof(Pole.Features.Functions.Application.Common.FunctionAppFeature).Assembly,
            typeof(Pole.Features.Functions.Runtime.Consumers.FunctionRuntimeFeature).Assembly,
            typeof(Pole.Features.RedisServices.Application.RedisServicesFeatureRegistration).Assembly,
            typeof(Pole.Features.Postgres.Application.PostgresFeatureRegistration).Assembly
        ]).Distinct().SelectMany(assembly => assembly.GetTypes())
        .Where(type => !type.IsAbstract && typeof(IMigration).IsAssignableFrom(type))
        .Select(type => new object[] { type.AssemblyQualifiedName! });

    [Fact]
    public async Task Migration_runner_acquires_lock_and_remembers_applied_migrations_after_reopen()
    {
        using var fixture = new SkryniaFixture();
        var migrations = Migrations.Select(row => (IMigration)Activator.CreateInstance(Type.GetType((string)row[0], true)!)!).ToArray();
        var logger = Microsoft.Extensions.Logging.Abstractions.NullLogger<MigrationRunner>.Instance;
        await new MigrationRunner(fixture.Database, logger, migrations).RunMigrations();
        fixture.Reopen();
        await new MigrationRunner(fixture.Database, logger, migrations).RunMigrations();
    }

    [Theory]
    [MemberData(nameof(Migrations))]
    public async Task Production_migration_can_run_and_reopen(string typeName)
    {
        using var fixture = new SkryniaFixture();
        var migration = (IMigration)Activator.CreateInstance(Type.GetType(typeName, throwOnError: true)!)!;
        // Later migrations are entitled to depend on the earlier schema in their feature.
        var preceding = migration.GetType().Assembly.GetTypes()
            .Where(type => !type.IsAbstract && typeof(IMigration).IsAssignableFrom(type))
            .Select(type => (IMigration)Activator.CreateInstance(type)!)
            .Where(candidate => candidate.Id.CompareTo(migration.Id) <= 0)
            .OrderBy(candidate => candidate.Id);
        foreach (var prerequisite in preceding)
            await prerequisite.Up(fixture.Database, CancellationToken.None);
        fixture.Reopen();
        await migration.Up(fixture.Database, CancellationToken.None);
    }
}
