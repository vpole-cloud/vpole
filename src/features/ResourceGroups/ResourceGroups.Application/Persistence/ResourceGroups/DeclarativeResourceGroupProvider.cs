using Pole.Features.GitOps.Primitives;
using Pole.Features.ResourceGroups.Events;
using Pole.Features.Events.Abstractions;
using System.Collections.Immutable;
using System.Text.Json;
using MongoDB.Driver;
using Pole.Features.GitOps.Contracts;
using Pole.Features.ResourceGroups.Application.Common;
using Pole.Features.ResourceGroups.Domain;

namespace Pole.Features.ResourceGroups.Application.Persistence.ResourceGroups;

public sealed record ResourceGroupDeclaration
{
    public string Description { get; init; } = "";

    public string[] Tags { get; init; } = [];
}

internal sealed partial class DeclarativeResourceGroupProvider(
    IMongoCollection<ResourceGroup> groups,
    IMongoCollection<IsolationScope> scopes,
    IGitOpsMutations git,
    StoreOutboxWrites outbox) : ITransactionalDeclarativeResourceProvider
{
    public string Kind => "ResourceGroup";

    public string DescribeFailure(IDeclarationFailure failure) => failure switch
    {
        ResourceGroupDeclarationRejected rejected => rejected.Reason switch
        {
            ResourceGroupDeclarationError.NameTooLong => "NameTooLong",
            ResourceGroupDeclarationError.ExplicitAdoptionRequired => "ExplicitAdoptionRequired",
            ResourceGroupDeclarationError.ResourceDeleting => "ResourceDeleting",
            ResourceGroupDeclarationError.ExpectedRevisionConflict => "ExpectedRevisionConflict",
            _ => throw new ArgumentOutOfRangeException(nameof(failure))
        },
        _ => throw new ArgumentException("The failure belongs to another declaration provider.", nameof(failure))
    };

    public int MaximumNameLength => ResourceGroupFactory.MaximumNameLength;

    public JsonElement NormalizeAndValidate(JsonElement spec)
    {
        var value = DeclarationJson.Read<ResourceGroupDeclaration>(spec);
        DeclarationJson.Require(value.Description is { Length: <= ResourceGroupFactory.MaximumDescriptionLength }
            && value.Tags is not null &&
            value.Tags.All(tag => tag is { Length: > 0 and <= ResourceGroupFactory.MaximumTagLength }
                && !string.IsNullOrWhiteSpace(tag)), "spec");

        return DeclarationJson.Write(value with
        {
            Description = value.Description.Trim(),
            Tags = value.Tags.Select(tag => tag.Trim()).Distinct().Order(StringComparer.Ordinal).ToArray()
        });
    }

    public IReadOnlyList<DeclarationStep> Steps(
        ResourceAddress address,
        JsonElement spec) => [new(
                DeclarationPhase.Prepare,
                [])];

    public async Task<string?> ResolveAsync(
        ResolveDeclaredResourceRequest request,
        IClientSessionHandle session,
        CancellationToken ct) =>
        (await groups.Find(
            session,
            ResourceGroupFilters.ByTechnicalName(request.TenantId, request.Name)).FirstOrDefaultAsync(ct))?.Id;

    public Task<GitOperationResult> ApplyAsync(GitOperation operation, CancellationToken ct) =>
        git.ExecuteAsync(operation, async (
                session,
                token) => (await ApplyDeclarationAsync(
                operation,
                session,
                token)) switch
            {
                ResourceGroupDeclarationApplied applied => new(applied.ResourceId, Changed: applied.Changed),
                ResourceGroupDeclarationRejected rejected => new(operation.ResourceId, rejected)
            }, ct);

    private async Task<ApplyResourceGroupDeclarationResult> ApplyDeclarationAsync(
        GitOperation operation, IClientSessionHandle session, CancellationToken token)
    {
        var context = operation.Context;
        var declaration = operation.Declaration;

        if (declaration.Address.Name.Length > MaximumNameLength)
            return new ResourceGroupDeclarationRejected(ResourceGroupDeclarationError.NameTooLong);

        var spec = DeclarationJson.Read<ResourceGroupDeclaration>(declaration.Spec);
        var current = await groups.Find(
            session,
            ResourceGroupFilters.ByTechnicalName(
                context.TenantId,
                declaration.Address.Name)).FirstOrDefaultAsync(token);

        if (current is not null && current.Id != operation.ResourceId)
            return new ResourceGroupDeclarationRejected(ResourceGroupDeclarationError.ExplicitAdoptionRequired);

        if (current is null)
        {
            var created = ResourceGroupFactory.Create(context.TenantId, declaration.Address.Name, spec.Description,
                ResourceGroupFactory.DefaultCreateOptions with
                {
                    Id = operation.ResourceId,
                    Tags = spec.Tags.ToImmutableHashSet()
                });
            var scope = IsolationScopeFactory.CreateDefault(context.TenantId, created.Id,
                new IsolationScopeFactory.CreateOptions(created.DefaultIsolationScopeId, created.CreatedAtUtc));
            await groups.InsertOneAsync(session, created, cancellationToken: token);
            await scopes.InsertOneAsync(session, scope, cancellationToken: token);
            await outbox(session, [OutboxWrite.Create(created.ToCreateEvent())], token);

            return new ResourceGroupDeclarationApplied(created.Id, true);
        }

        if (current.Lifecycle is not ActiveResourceGroupLifecycleState)
            return new ResourceGroupDeclarationRejected(ResourceGroupDeclarationError.ResourceDeleting);

        var replacement = current.UpdateMetadata(spec.Description, spec.Tags.ToImmutableHashSet(), DateTime.UtcNow);

        if (replacement.Revision == current.Revision)
            return new ResourceGroupDeclarationApplied(current.Id, false);

        var result = await groups.ReplaceOneAsync(
            session,
            ResourceGroupFilters.AtRevisionAndRegistrationFence(current),
            replacement,
            cancellationToken: token);

        if (result.ModifiedCount != 1)
            return new ResourceGroupDeclarationRejected(ResourceGroupDeclarationError.ExpectedRevisionConflict);

        await outbox(session, [OutboxWrite.Create(new ResourceGroupUpdatedEvent(
                        replacement.Id, replacement.Name, replacement.Description,
                        new ResourceGroupUpdatedEventChanges(
                            UpdateLog<string>.Of(current.Description, replacement.Description),
                            UpdateLog<ImmutableHashSet<string>>.Of(
                                current.Tags,
                                replacement.Tags)), replacement.Revision)
                    { EventCore = EventCore.Create(context.TenantId) })], token);

        return new ResourceGroupDeclarationApplied(current.Id, true);
    }
}
