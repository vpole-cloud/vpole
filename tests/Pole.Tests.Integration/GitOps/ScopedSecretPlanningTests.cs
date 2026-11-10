using MongoDB.Driver;
using Pole.Features.GitOps.Compiler;
using Pole.Tests.Integration.Infrastructure;
using Xunit;

namespace Pole.Tests.Integration.GitOps;

[Collection(IntegrationTestCollection.Tenant2)]
public sealed class ScopedSecretPlanningTests(
    MongoReplicaSetFixture fixture
) : DeclarativePipelineTestBase(fixture)
{
    [Fact]
    public async Task Secret_uses_accepted_named_scope_and_next_run_is_no_op()
    {
        var files = IsolationSnapshot()
            .Append(
                new ManifestFile(
                    "secret.yaml",
                    """
                    apiVersion: pole.io/v1alpha1
                    kind: Secret
                    metadata: {name: scoped-credentials}
                    spec:
                      resourceGroup: scope-group
                      isolationScope: tenant-private
                      purpose: scoped
                      keys: [password]
                    """))
            .ToArray();
        var applied = await Apply(files);
        Assert.Equal(RepositoryPipelineStatus.ConfigurationApplied, applied.Status);
        var scope = await DbContext.IsolationScopes
            .Find(item => item.TenantId == Data.TenantId && item.Name == "tenant-private")
            .SingleAsync();
        var secret = await Secrets.Find(item => item.TenantId == Data.TenantId && item.Name == "scoped-credentials")
            .SingleAsync();
        Assert.Equal(scope.Id, secret.IsolationScopeId);
        var events = await BusinessEvents();
        Assert.Equal(RepositoryPipelineStatus.ConfigurationApplied, (await Apply(files)).Status);
        Assert.Equal(events, await BusinessEvents());
        var current = await Secrets.Find(item => item.Id == secret.Id).SingleAsync();
        Assert.Equal(secret.Revision, current.Revision);
        Assert.Equal(secret.CurrentVersion, current.CurrentVersion);
        Assert.Equal(secret.UpdatedAtUtc, current.UpdatedAtUtc);
    }

    [Fact]
    public async Task Secret_cannot_move_from_default_to_named_scope()
    {
        var prefix = IsolationSnapshot();

        static ManifestFile SecretFile(bool named) => new(
            "secret.yaml",
            $$"""
              apiVersion: pole.io/v1alpha1
              kind: Secret
              metadata: {name: scoped-credentials}
              spec:
                resourceGroup: scope-group
                {{(named ? "isolationScope: tenant-private" : "")}}
                keys: [password]
              """);

        Assert.Equal(
            RepositoryPipelineStatus.ConfigurationApplied,
            (await Apply(prefix.Append(SecretFile(false)).ToArray())).Status);
        var original = await Secrets.Find(item => item.TenantId == Data.TenantId && item.Name == "scoped-credentials")
            .SingleAsync();
        var blocked = await Apply(prefix.Append(SecretFile(true)).ToArray());
        Assert.Equal(RepositoryPipelineStatus.Blocked, blocked.Status);
        var current = await Secrets.Find(item => item.Id == original.Id).SingleAsync();
        Assert.Equal(original.IsolationScopeId, current.IsolationScopeId);
        Assert.Equal(original.Revision, current.Revision);
    }
}
