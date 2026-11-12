using System.Text.Json;
using MongoDB.Driver;
using Pole.Features.Backups.Application.Configuration;
using Pole.Features.Backups.Domain;
using Pole.Features.GitOps.Contracts;
using Pole.Features.GitOps.Primitives;
using Pole.Features.ResourceGroups.Contracts;
using Pole.Features.Secrets.Contracts;
using Pole.Workloads;

namespace Pole.Features.Backups.Application.Declarations;

internal sealed record BackupStorageKeysDeclaration(
    string Secret,
    string AccessKeyIdKey,
    string SecretAccessKeyKey
);

internal sealed record BackupDestinationDeclaration(
    string ResourceGroup,
    string? IsolationScope,
    string Endpoint,
    string Bucket,
    string Prefix,
    BackupStorageKeysDeclaration Credentials
);

internal sealed class DeclarativeBackupDestinationProvider(
    IMongoCollection<BackupDestination> destinations,
    IGitOpsMutations git,
    BackupConfigurationWriter writer,
    BackupConfigurationDeletions deletions,
    LoadIsolationScopePlanningSnapshot loadScope,
    FenceIsolationScopePlanningSnapshot fenceScope,
    LoadConsumerSecretPlanningSnapshot loadSecret,
    FenceConsumerSecretPlanningSnapshot fenceSecret
) : BackupDeclarationProvider<BackupDestination, BackupDestinationInput>(destinations, git),
    IDeclarativeRegisteredResourceProvider
{
    internal const string DeclarationKind = "BackupDestination";
    public override string Kind => DeclarationKind;
    public ResourceTypeId ResourceType => BackupConfigurationWriter.DestinationType;
    protected override GitLifecycleProtection Protection => GitLifecycleProtection.ProtectedData;

    protected override UpdateDefinition<BackupDestination> PlanningFence =>
        Builders<BackupDestination>.Update.Inc(d => d.ReferenceFence, 1);

    public override JsonElement NormalizeAndValidate(JsonElement spec)
    {
        var value = DeclarationJson.Read<BackupDestinationDeclaration>(spec);
        DeclarationJson.Require(DeclarationNames.Valid(value.ResourceGroup), "spec.resourceGroup");
        DeclarationJson.Require(
            value.IsolationScope is null ||
            value.IsolationScope != "default" && DeclarationNames.Valid(value.IsolationScope),
            "spec.isolationScope");
        DeclarationJson.Require(
            BackupInputRules.ValidStorageLocation(value.Endpoint, value.Bucket, value.Prefix),
            "spec.endpoint");
        DeclarationJson.Require(
            value.Credentials is not null && DeclarationNames.Valid(value.Credentials.Secret) &&
            BackupInputRules.ValidKeys(value.Credentials.AccessKeyIdKey, value.Credentials.SecretAccessKeyKey),
            "spec.credentials");
        return DeclarationJson.Write(value with { Prefix = value.Prefix.Trim('/') });
    }

    public override DeclarationFacts Facts(JsonElement spec)
    {
        var value = DeclarationJson.Read<BackupDestinationDeclaration>(spec);
        var secret = new ResourceAddress("Secret", value.Credentials.Secret);
        var refs = new List<DeclaredReference>
        {
            new(secret, "spec.credentials.accessKeyIdKey", value.Credentials.AccessKeyIdKey, RequiresAccess: true),
            new(
                secret,
                "spec.credentials.secretAccessKeyKey",
                value.Credentials.SecretAccessKeyKey,
                RequiresAccess: true),
        };
        if (value.IsolationScope is not null)
            refs.Add(new(new("IsolationScope", value.IsolationScope), "spec.isolationScope"));
        return new(new("ResourceGroup", value.ResourceGroup), [], refs, [], []);
    }

    public override IReadOnlyList<DeclarationStep> Steps(ResourceAddress address, JsonElement spec)
    {
        var facts = Facts(spec);
        var deps = facts.References
            .Select(r => r.Target)
            .Append(facts.Scope!)
            .Distinct()
            .Select(r => new DeclarationDependency(r, DeclarationPhase.Prepare))
            .ToArray();
        return
        [
            new(DeclarationPhase.Prepare, deps),
            new(DeclarationPhase.Configure, [new(address, DeclarationPhase.Prepare)])
        ];
    }

    protected override DeclarationProjection Project(BackupDestination destination) => Project(
        destination.ResourceGroupId,
        destination.IsolationScopeId,
        destination.Storage);

    private static DeclarationProjection Project(
        string group,
        string scope,
        BackupStorage storage) => new(
        DeclarationJson.Write(new { resourceGroupId = group, isolationScopeId = scope, storage }),
        DeclarationJson.Write(new { }));

    protected override async Task<ResolvedBackupDeclaration<BackupDestinationInput>?> ResolveAsync(
        BackupDeclarationRead read,
        bool fence)
    {
        var value = DeclarationJson.Read<BackupDestinationDeclaration>(read.Declaration.Spec);
        var groupId = await read.ResolveAsync(new("ResourceGroup", value.ResourceGroup));
        var secretId = await read.ResolveAsync(new("Secret", value.Credentials.Secret));
        var scopeId = value.IsolationScope is null
            ? null
            : await read.ResolveAsync(new("IsolationScope", value.IsolationScope));
        if (groupId is null || secretId is null || value.IsolationScope is not null && scopeId is null ||
            await loadScope(new(read.TenantId, groupId, scopeId), read.Session, read.CancellationToken) is not
                IsolationScopePlanningSnapshot scope)
            return null;
        if (fence && await fenceScope(scope, read.Session, read.CancellationToken) is not IsolationScopePlanningFenced)
            return null;

        List<DeclarationDocumentVersion> deps =
        [
            new(ResourceGroupVersionKeys.Revision(groupId), scope.ResourceGroupRevision),
            new(ResourceGroupVersionKeys.IsolationScopeRevision(scope.IsolationScopeId), scope.ScopeRevision)
        ];
        var input = new BackupDestinationInput(
            read.Declaration.Address.Name,
            groupId,
            scope.IsolationScopeId,
            value.Endpoint,
            value.Bucket,
            value.Prefix,
            secretId,
            value.Credentials.AccessKeyIdKey,
            value.Credentials.SecretAccessKeyKey);
        // Configure follows SecretBinding.Bind. Prepare must first register this consumer so its grant can exist.
        if (read.Operation is { Phase: DeclarationPhase.Configure } operation)
        {
            var verification = await loadSecret(
                new(
                    read.TenantId,
                    groupId,
                    scope.IsolationScopeId,
                    ResourceType,
                    operation.ResourceId,
                    [],
                    [new(secretId, input.AccessKeyIdKey), new(secretId, input.SecretAccessKeyKey)],
                    []),
                read.Session,
                read.CancellationToken);
            if (verification is not ConsumerSecretPlanningSnapshot snapshot || fence &&
                await fenceSecret(snapshot, read.Session, read.CancellationToken) is not ConsumerSecretPlanningFenced)
                return null;
            foreach (var version in snapshot.Secrets)
            {
                deps.Add(new(SecretVersionKeys.Revision(version.SecretId), version.Revision));
                deps.Add(new(SecretVersionKeys.ReferenceFence(version.SecretId), version.ReferenceFence));
            }
        }

        return new(input, Project(groupId, scope.IsolationScopeId, input.Storage()) with { Dependencies = new(deps) });
    }

    protected override Task<BackupWriteResult<BackupDestination>> SaveAsync(
        BackupConfigurationWrite<BackupDestinationInput> write,
        IClientSessionHandle session,
        CancellationToken ct) => writer.SaveDestinationAsync(write, session, ct);

    protected override Task<BackupWriteResult<bool>> DeleteAsync(
        string tenantId,
        string id,
        long revision,
        IClientSessionHandle session,
        CancellationToken ct) => deletions.DeleteDestinationAsync(tenantId, id, revision, session, ct);
}
