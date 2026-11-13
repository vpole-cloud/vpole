using System.Text.Json;
using Pole.Features.Backups.Application.Declarations;
using Pole.Features.Backups.Contracts;
using Pole.Features.GitOps.Contracts;
using Pole.Features.GitOps.Primitives;
using Xunit;

namespace Pole.Features.Backups.Tests;

public class BackupDeclarationTests
{
    private static DeclarativeBackupDestinationProvider Destination() => new(
        null!,
        null!,
        null!,
        null!,
        null!,
        null!,
        null!,
        null!);

    private static DeclarativeBackupPolicyProvider Policy() => new(
        null!,
        null!,
        null!,
        null!,
        null!,
        [new SampleProvider()],
        null!,
        null!,
        null!,
        null!);

    [Fact]
    public void Destination_prepares_registered_consumer_before_configuring_its_secret_grant()
    {
        var provider = Destination();
        var address = new ResourceAddress(provider.Kind, "archive");
        var spec = provider.NormalizeAndValidate(
            DeclarationJson.Write(
                new
                {
                    resourceGroup = "shop", isolationScope = "data", endpoint = "https://storage.example",
                    bucket = "tenant-bucket", prefix = "tenant/",
                    credentials = new
                        { secret = "archive-keys", accessKeyIdKey = "access", secretAccessKeyKey = "secret" },
                }));
        Assert.Equal("tenant", spec.GetProperty("prefix").GetString());
        var facts = provider.Facts(spec);
        Assert.Equal(new ResourceAddress("ResourceGroup", "shop"), facts.Scope);
        Assert.Equal(["access", "secret"], facts.References.Where(r => r.RequiresAccess).Select(r => r.RequiredKey));
        var steps = provider.Steps(address, spec);
        Assert.Equal(
            new DeclarationDependency(new("Secret", "archive-keys"), DeclarationPhase.Prepare),
            Assert.Single(steps[0].Dependencies, d => d.Resource.Kind == "Secret"));
        Assert.Equal(new(address, DeclarationPhase.Prepare), Assert.Single(steps[1].Dependencies));
        Assert.Equal(DeclarationPhase.Configure, steps[1].Phase);
    }

    [Theory]
    [InlineData("https://user:pass@s3.example", "logs/", "spec.endpoint")]
    [InlineData("https://s3.example", "../secret", "spec.prefix")]
    public void Destination_rejects_credentials_in_urls_and_relative_parent_paths(
        string endpoint,
        string prefix,
        string field)
    {
        var error = Assert.Throws<DeclarationSchemaException>(() => Destination()
            .NormalizeAndValidate(
                DeclarationJson.Write(
                    new
                    {
                        resourceGroup = "shop", endpoint, bucket = "tenant", prefix,
                        credentials = new { secret = "archive-keys", accessKeyIdKey = "a", secretAccessKeyKey = "b" },
                    })));
        Assert.Equal(field, error.Field);
    }

    [Fact]
    public void Policy_uses_contributed_service_kind_and_requires_service_and_destination_configuration()
    {
        var provider = Policy();
        var address = new ResourceAddress(provider.Kind, "daily");
        var service = new ResourceAddress("OtherService", "store");
        var spec = provider.NormalizeAndValidate(
            DeclarationJson.Write(
                new { service, destination = "archive", schedule = "0 1 * * *", retentionDays = 30 }));
        var facts = provider.Facts(spec);
        Assert.Equal(service, Assert.Single(facts.ExclusiveRelations).Left);
        var step = Assert.Single(provider.Steps(address, spec));
        Assert.Equal(DeclarationPhase.Configure, step.Phase);
        Assert.Contains(new(service, DeclarationPhase.Configure), step.Dependencies);
        Assert.Contains(new(new("BackupDestination", "archive"), DeclarationPhase.Configure), step.Dependencies);
        Assert.False(spec.GetProperty("suspended").GetBoolean());
    }

    [Theory]
    [InlineData("Unknown", "0 1 * * *", 30, "spec.service")]
    [InlineData("OtherService", "invalid", 30, "spec.schedule")]
    [InlineData("OtherService", "0 1 * * *", 0, "spec.retentionDays")]
    public void Policy_rejects_invalid_inputs(
        string kind,
        string schedule,
        int retentionDays,
        string field)
    {
        var error = Assert.Throws<DeclarationSchemaException>(() => Policy()
            .NormalizeAndValidate(
                DeclarationJson.Write(
                    new
                    {
                        service = new ResourceAddress(kind, "source"), destination = "archive", schedule, retentionDays,
                    })));
        Assert.Equal(field, error.Field);
    }

    [Fact]
    public void History_and_restore_commands_are_not_declarative_fields() => Assert.Throws<JsonException>(() => Policy()
        .NormalizeAndValidate(
            DeclarationJson.Write(
                new
                {
                    service = new ResourceAddress("OtherService", "source"), destination = "archive",
                    schedule = "0 1 * * *", retentionDays = 7,
                    restoreTo = "now",
                })));

    private sealed class SampleProvider : IBackupProvider
    {
        public string DeclarationKind => "OtherService";
        public string ResourceType => "example/other";
        public bool SupportsPointInTimeRecovery => false;

        public ValidateBackupScheduleOutcome ValidateSchedule(BackupSchedule schedule) => schedule.RetentionDays > 0
            ? new BackupScheduleAccepted()
            : new BackupRetentionInvalid();

        public Task<DescribeBackupSourceOutcome> DescribeAsync(BackupSourceIdentity source, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<BackupProviderResult<BackupProviderProgress>> ApplyScheduleAsync(
            ApplyBackupScheduleRequest request,
            CancellationToken ct) => throw new NotSupportedException();

        public Task<BackupProviderResult<IReadOnlyList<BackupCatalogEntry>>> ListAsync(
            ListBackupCatalogRequest request,
            CancellationToken ct) => throw new NotSupportedException();

        public Task<BackupProviderResult<BackupRuntimeObservation>> ObserveAsync(
            BackupArchiveContext archive,
            CancellationToken ct) => throw new NotSupportedException();

        public Task<BackupProviderResult<BackupProviderProgress>> RunAsync(
            BackupCopyRequest request,
            CancellationToken ct) => throw new NotSupportedException();

        public Task<BackupProviderResult<BackupProviderProgress>> RestoreAsync(
            RestoreBackupRequest request,
            CancellationToken ct) => throw new NotSupportedException();
    }
}
