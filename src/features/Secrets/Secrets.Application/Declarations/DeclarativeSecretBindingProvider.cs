using Pole.Features.Secrets.Application.Values;
using Pole.Features.Secrets.Application.Secrets;
using Pole.Features.Secrets.Application.References;
using Pole.Features.Secrets.Application.Projections;
using Pole.Features.Secrets.Application.Mongo;
using Pole.Features.Secrets.Application.Managed;
using Pole.Features.Secrets.Application.Lifecycle;
using Pole.Features.Secrets.Application.Bootstrap;
using Pole.Features.Secrets.Application.Bindings;
using Pole.Features.Secrets.Application;
using System.Text.Json;
using System.Text.Json.Serialization;
using MongoDB.Driver;
using Pole.Features.GitOps.Contracts;
using Pole.Features.GitOps.Primitives;
using Pole.Features.Secrets.Contracts;
using Pole.Features.Secrets.Primitives;

namespace Pole.Features.Secrets.Application.Declarations;

public sealed record SecretBindingDeclaration
{
    public required string Secret { get; init; }

    public required ResourceAddress Consumer { get; init; }

    [JsonConverter(typeof(JsonStringEnumConverter<SecretBindingUsage>))]
    public SecretBindingUsage Usage { get; init; } = SecretBindingUsage.Runtime;

    public Dictionary<string, string> KeyMappings { get; init; } = [];
}

internal sealed class DeclarativeSecretBindingProvider(
    SecretBindingConsumerCatalog consumers,
    SecretBindingPlanning planning,
    SecretBindingExecution execution,
    SecretBindingAdoption adoption,
    SecretBindingLifecycle lifecycle,
    SecretBindingCandidates candidates) : ITransactionalDeclarativeResourceProvider,
    IDeclarativeAdoptionPreviewProvider, IDeclarativeResourceAdoptionProvider, IDeclarativeResourceLifecycleProvider,
    IDeclarativeAdoptionCandidateProvider
{
    public string Kind => "SecretBinding";

    public string DescribeFailure(IDeclarationFailure failure) => failure switch
    {
        SecretBindingDeclarationRejected rejected => rejected.Reason switch
        {
            SecretBindingDeclarationError.BindingPrerequisiteUnavailable => "BindingPrerequisiteUnavailable",
            SecretBindingDeclarationError.SecretUnavailable => "SecretUnavailable",
            SecretBindingDeclarationError.ImmutableBindingChange => "ImmutableBindingChange",
            SecretBindingDeclarationError.ExplicitAdoptionRequired => "ExplicitAdoptionRequired",
            SecretBindingDeclarationError.SecretKeyUnavailable => "SecretKeyUnavailable",
            SecretBindingDeclarationError.BindingScopeUnavailable => "BindingScopeUnavailable",
            SecretBindingDeclarationError.SecretBindingLimitReached => "SecretBindingLimitReached",
            SecretBindingDeclarationError.SecretKeysInUse => "SecretKeysInUse",
            _ => throw new ArgumentOutOfRangeException(nameof(failure))
        },
        _ => throw new ArgumentException("The failure belongs to another declaration provider.", nameof(failure))
    };

    public DeclarationFacts Facts(JsonElement spec)
    {
        var value = DeclarationJson.Read<SecretBindingDeclaration>(spec);

        return new(null,
            [],
            [
                new(new("Secret", value.Secret), "spec.secret"),
                new(value.Consumer, "spec.consumer"),
                .. value.KeyMappings.Keys.Select(key => new DeclaredReference(
                    new("Secret", value.Secret), "spec.keyMappings", key))
            ],
            ExclusiveRelations:
            [
                new(SecretDeclarationRules.ConsumerBindingRelation, new("Secret", value.Secret), value.Consumer)
            ],
            AccessGrants:
            [
                new(value.Consumer, new("Secret", value.Secret), value.KeyMappings.Keys.ToArray(),
                    value.KeyMappings.Count == 0)
            ]);
    }

    public JsonElement NormalizeAndValidate(JsonElement spec)
        => consumers.NormalizeAndValidate(spec);

    public IReadOnlyList<DeclarationStep> Steps(ResourceAddress address, JsonElement spec)
    {
        var value = DeclarationJson.Read<SecretBindingDeclaration>(spec);
        return
        [
            new(DeclarationPhase.Bind,
            [
                new(new("Secret", value.Secret), DeclarationPhase.Prepare),
                new(value.Consumer, DeclarationPhase.Prepare)
            ])
        ];
    }

    public Task<string?> ResolveAsync(ResolveDeclaredResourceRequest request,
        IClientSessionHandle session, CancellationToken ct) => planning.ResolveAsync(request, session, ct);

    public Task<ReadCurrentDeclarationOutcome> ReadCurrentAsync(ReadCurrentDeclarationRequest request,
        IClientSessionHandle session, CancellationToken ct) => planning.ReadCurrentAsync(request, session, ct);

    public Task<ProjectDesiredDeclarationOutcome> ProjectDesiredAsync(ProjectDesiredDeclarationRequest request,
        IClientSessionHandle session, CancellationToken ct) => planning.ProjectDesiredAsync(request, session, ct);

    public Task<FenceDeclarationOutcome> FenceAsync(FenceDeclarationRequest request,
        IClientSessionHandle session, CancellationToken ct) => planning.FenceAsync(request, session, ct);

    public Task<GitOperationResult> ApplyAsync(GitOperation operation, CancellationToken ct) =>
        execution.ApplyAsync(operation, ct);

    public Task<ReadLifecycleResourceOutcome> ReadLifecycleAsync(ReadLifecycleResourceRequest request,
        IClientSessionHandle session, CancellationToken ct) => lifecycle.ReadLifecycleAsync(request, session, ct);

    public Task<RemoveLifecycleResourceOutcome> RemoveAsync(RemoveLifecycleResourceRequest request,
        IClientSessionHandle session, CancellationToken ct) => lifecycle.RemoveAsync(request, session, ct);

    public Task<PreviewLifecycleAdoptionOutcome> PreviewAdoptionAsync(PreviewLifecycleAdoptionRequest request,
        IClientSessionHandle session, CancellationToken ct) => adoption.PreviewAdoptionAsync(request, session, ct);

    public Task<AdoptLifecycleResourceOutcome> AdoptAsync(AdoptLifecycleResourceRequest request,
        IClientSessionHandle session, CancellationToken ct) => adoption.AdoptAsync(request, session, ct);

    public Task<DeclarationAdoptionCandidatePage> FindAdoptionCandidatesAsync(
        FindDeclarationAdoptionCandidatesRequest request, IClientSessionHandle session, CancellationToken ct) =>
        candidates.FindAdoptionCandidatesAsync(request, session, ct);
}
