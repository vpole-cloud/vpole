using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Bson;
using MongoDB.Driver;
using Pole.Features.GitOps.Contracts;
using Pole.Features.Secrets.Application.Managed;
using Pole.Features.Secrets.Contracts;
using Pole.Features.Secrets.Domain;
using Pole.Features.Secrets.Primitives;
using Pole.Tests.Integration.Infrastructure;
using Xunit;

namespace Pole.Tests.Integration.Secrets;

public sealed partial class ManagedSecretIntegrationTests
{
    [Fact]
    public async Task Unnamed_binding_preview_uses_selected_id_and_preserves_pins_without_writes()
    {
        var group = await DbContext.InsertResourceGroupAsync(Data.SeedResourceGroup());
        var owner = new ManagedSecretOwner("pole/postgresql", Id());
        using var session = await Fixture.Client.StartSessionAsync();
        session.StartTransaction();
        await StageConsumer(group, owner, session);
        await session.CommitTransactionAsync();
        var binding = SecretBinding.Create(
            Id(),
            owner.ResourceType,
            owner.ResourceId,
            new AllSecretDeployments(),
            [],
            SecretBindingUsage.Bootstrap);
        var secret = SecretFactory.CreateMetadata(
                Id(),
                group.TenantId,
                group.Id,
                group.DefaultIsolationScopeId,
                "source-password",
                "bootstrap",
                ["password"],
                DateTime.UtcNow)
            .Bind(binding, DateTime.UtcNow);
        var secrets = Services.GetRequiredService<IMongoCollection<Secret>>();
        await secrets.InsertOneAsync(secret);
        var declaration = new CompiledDeclaration(
            new ResourceAddress("SecretBinding", "adopted-access"),
            DeclarationJson.Write(
                new
                {
                    secret = secret.Name,
                    consumer = new { kind = "PostgresCluster", name = "database" },
                    usage = "Bootstrap",
                    keyMappings = new Dictionary<string, string>(),
                }),
            "hash",
            [],
            "secret.yaml",
            1);
        var request = new PreviewLifecycleAdoptionRequest(
            group.TenantId,
            declaration,
            binding.Id,
            [
                new GitPreviewResolvedReference(new ResourceAddress("Secret", secret.Name), secret.Id),
                new GitPreviewResolvedReference(new ResourceAddress("PostgresCluster", "database"), owner.ResourceId),
            ]);
        var provider = Assert.IsAssignableFrom<IDeclarativeAdoptionPreviewProvider>(
            Services.GetServices<IDeclarativeResourceProvider>().Single(item => item.Kind == "SecretBinding"));
        session.StartTransaction();
        var preview = Assert.IsType<GitLifecycleAdoptionPreview>(
            (await provider.PreviewAdoptionAsync(request, session, default)).Value);
        Assert.Equal(binding.Id, preview.Current.ResourceId);
        Assert.Equal(2, preview.ResolvedReferences.Count);
        Assert.IsType<DeclarationReadBlocked>(
            (await provider.PreviewAdoptionAsync(
                request with
                {
                    ResourceId = Id(),
                },
                session,
                default)).Value);
        await session.AbortTransactionAsync();
        Assert.Equal(
            secret.ToBsonDocument(),
            (await secrets.Find(item => item.Id == secret.Id).SingleAsync()).ToBsonDocument());
        Assert.Equal(0, await DbContext.CountOutboxMessagesAsync(Data.TenantId, "pole.secrets/v1/secret-changed"));
    }

    [Fact]
    public async Task Adoption_checks_identity_inside_Secrets_and_returns_only_fenced_metadata()
    {
        var group = await DbContext.InsertResourceGroupAsync(Data.SeedResourceGroup());
        var owner = new ManagedSecretOwner("pole/postgresql", Id());
        var prepared = Assert.IsType<PreparedManagedSecret>(
            (await Services.GetRequiredService<PrepareManagedSecret>()(
                new PrepareManagedSecretRequest(
                    group.TenantId,
                    Id(),
                    owner,
                    "adoption-credentials",
                    1,
                    new Dictionary<string, string>
                        { ["username"] = "application", ["password"] = "protected-password" },
                    ManagedSecretKubernetesType.BasicAuthentication),
                default)).Value);
        using var session = await Fixture.Client.StartSessionAsync();
        session.StartTransaction();
        var deployment = await StageConsumer(group, owner, session);
        Assert.IsType<ManagedSecretMutationSucceeded>(
            (await Services.GetRequiredService<CreateBoundManagedSecret>()(
                new CreateBoundManagedSecretRequest(prepared, group.Id, group.DefaultIsolationScopeId, deployment.Id),
                session,
                default)).Value);
        await session.CommitTransactionAsync();

        var request = new ReadManagedSecretAdoptionRequest(
            group.TenantId,
            owner,
            group.Id,
            group.DefaultIsolationScopeId,
            new ManagedSecretIdentityExpectation("username", "application"));
        session.StartTransaction();
        var snapshot = Assert.IsType<ManagedSecretAdoptionSnapshot>(
            (await Services.GetRequiredService<ReadManagedSecretAdoption>()(request, session, default)).Value);
        Assert.Equal(prepared.SecretId, snapshot.SecretId);
        Assert.DoesNotContain("protected-password", JsonSerializer.Serialize(snapshot));
        Assert.IsType<ManagedSecretIdentityMismatch>(
            (await Services.GetRequiredService<ReadManagedSecretAdoption>()(
                request with { Identity = new ManagedSecretIdentityExpectation("username", "different") },
                session,
                default)).Value);
        Assert.IsType<ManagedSecretUnavailable>(
            (await Services.GetRequiredService<ReadManagedSecretAdoption>()(
                request with { TenantId = Id() },
                session,
                default)).Value);
        Assert.IsType<ManagedSecretAdoptionFenced>(
            (await Services.GetRequiredService<FenceManagedSecretAdoption>()(snapshot, session, default)).Value);
        await session.AbortTransactionAsync();

        var secret = (await Services.GetRequiredService<LoadManagedSecret>()(group.TenantId, owner, default))!;
        Assert.Equal(snapshot.ReferenceFence, secret.ReferenceFence);
        var values = Assert.IsType<ManagedSecretValues>(
            (await Services.GetRequiredService<ReadManagedSecret>()(group.TenantId, owner, default)).Value);
        Assert.Equal("protected-password", values.Values["password"]);
    }

    [Fact]
    public async Task Adoption_fence_rejects_rotation_since_preview()
    {
        var group = await DbContext.InsertResourceGroupAsync(Data.SeedResourceGroup());
        var owner = new ManagedSecretOwner("pole/postgresql", Id());
        var prepared = Assert.IsType<PreparedManagedSecret>(
            (await Services.GetRequiredService<PrepareManagedSecret>()(
                new PrepareManagedSecretRequest(
                    group.TenantId,
                    Id(),
                    owner,
                    "adoption-rotation",
                    1,
                    new Dictionary<string, string> { ["username"] = "application", ["password"] = "old-password" },
                    ManagedSecretKubernetesType.BasicAuthentication),
                default)).Value);
        using var session = await Fixture.Client.StartSessionAsync();
        session.StartTransaction();
        var deployment = await StageConsumer(group, owner, session);
        Assert.IsType<ManagedSecretMutationSucceeded>(
            (await Services.GetRequiredService<CreateBoundManagedSecret>()(
                new CreateBoundManagedSecretRequest(prepared, group.Id, group.DefaultIsolationScopeId, deployment.Id),
                session,
                default)).Value);
        await session.CommitTransactionAsync();

        session.StartTransaction();
        var snapshot = Assert.IsType<ManagedSecretAdoptionSnapshot>(
            (await Services.GetRequiredService<ReadManagedSecretAdoption>()(
                new ReadManagedSecretAdoptionRequest(
                    group.TenantId,
                    owner,
                    group.Id,
                    group.DefaultIsolationScopeId,
                    new ManagedSecretIdentityExpectation("username", "application")),
                session,
                default)).Value);
        await session.CommitTransactionAsync();
        await Services.GetRequiredService<IMongoCollection<Secret>>()
            .UpdateOneAsync(
                item => item.Id == prepared.SecretId,
                Builders<Secret>.Update.Inc(item => item.CurrentVersion, 1));

        session.StartTransaction();
        Assert.IsType<ManagedSecretChanged>(
            (await Services.GetRequiredService<FenceManagedSecretAdoption>()(snapshot, session, default)).Value);
        await session.AbortTransactionAsync();
    }
}
