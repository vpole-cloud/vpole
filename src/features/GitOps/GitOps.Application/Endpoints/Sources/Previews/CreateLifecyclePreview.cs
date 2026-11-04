using System.Text.Json.Serialization;
using MongoDB.Bson;
using FluentValidation;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Pole.ASP.Helpers;
using Pole.Features.Core.Multitenancy;
using Pole.Features.GitOps.Application.Persistence;
using Pole.Features.GitOps.Contracts;
using Pole.Features.GitOps.Domain;
using Pole.Features.GitOps.Primitives;
using Pole.Mediator;
using Pole.Validation;
using System.Collections.Immutable;

namespace Pole.Features.GitOps.Application.Endpoints.Sources.Previews;

[JsonPolymorphic(TypeDiscriminatorPropertyName = "intent")]
[JsonDerivedType(typeof(AdoptRepositoryPreviewBody), "Adoption")]
[JsonDerivedType(typeof(RemoveRepositoryPreviewBody), "Removal")]
[JsonDerivedType(typeof(ReplaceRepositoryPreviewBody), "Replacement")]
[JsonDerivedType(typeof(RelocateRepositoryPreviewBody), "Relocation")]
[JsonDerivedType(typeof(DetachRepositoryPreviewBody), "Detach")]
[JsonDerivedType(typeof(ResourceChangesPreviewBody), "ResourceChanges")]
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public closed record CreateRepositoryPreviewBody(long ExpectedRevision);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record PreviewResourceSelection(
    string Kind,
    string Name,
    string ResourceId);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record PreviewDesiredAddress(
    string Kind,
    string Name);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record PreviewReplacementSelection(
    PreviewResourceSelection Original,
    PreviewDesiredAddress Desired);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record PreviewRepositoryLocation(
    string Provider,
    string ConnectionId,
    string ExternalRepositoryId,
    string Branch,
    string ManifestDirectory);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record AdoptRepositoryPreviewBody(
    long ExpectedRevision,
    IReadOnlyList<PreviewResourceSelection> Resources) : CreateRepositoryPreviewBody(ExpectedRevision);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record RemoveRepositoryPreviewBody(
    long ExpectedRevision,
    IReadOnlyList<PreviewResourceSelection> Resources) : CreateRepositoryPreviewBody(ExpectedRevision);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ReplaceRepositoryPreviewBody(
    long ExpectedRevision,
    IReadOnlyList<PreviewReplacementSelection> Replacements) : CreateRepositoryPreviewBody(ExpectedRevision);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record RelocateRepositoryPreviewBody(
    long ExpectedRevision,
    PreviewRepositoryLocation Destination) : CreateRepositoryPreviewBody(ExpectedRevision);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record DetachRepositoryPreviewBody(long ExpectedRevision) : CreateRepositoryPreviewBody(ExpectedRevision);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ResourceChangesPreviewBody(
    long ExpectedRevision,
    [property: JsonConverter(typeof(ResourceChangesRemovalJsonConverter))]
    GitPreviewRemovalDisposition Removal = GitPreviewRemovalDisposition.Remove)
    : CreateRepositoryPreviewBody(ExpectedRevision);

public sealed class ResourceChangesRemovalJsonConverter : JsonStringEnumConverter<GitPreviewRemovalDisposition>
{
    public ResourceChangesRemovalJsonConverter() : base(allowIntegerValues: false) { }
}

public sealed record CreateLifecyclePreviewRequest(
    [FromRoute] string Id,
    [FromBody] CreateRepositoryPreviewBody Body,
    [FromTenantHeader] string TenantId)
    : IMediatorRequest<CreateLifecyclePreviewOutcome>, ITenantWriteOperation;

public union CreateLifecyclePreviewOutcome(
    LifecyclePreviewView,
    GitSourceNotFound,
    GitSourceRevisionConflict,
    GitSourceBusy,
    GitPreviewReadFailed,
    GitPreviewRejected);

public sealed class CreateLifecyclePreviewValidator : AbstractValidator<CreateLifecyclePreviewRequest>
{
    public CreateLifecyclePreviewValidator()
    {
        RuleFor(item => item.Id).MongoObjectId("Source ID");
        RuleFor(item => item.Body).NotNull();
        When(item => item.Body is not null, () =>
        {
            RuleFor(item => item.Body.ExpectedRevision).GreaterThan(0);
            RuleFor(item => item.Body).Custom((body, context) =>
            {
                if (body is ResourceChangesPreviewBody changes && !Enum.IsDefined(changes.Removal))
                    context.AddFailure("removal", "Choose Remove or Retain for declarations absent from the snapshot.");

                if (body is ReplaceRepositoryPreviewBody invalid && invalid.Replacements is not null &&
                    invalid.Replacements.Any(item => item is null || item.Original is null || item.Desired is null))
                {
                    context.AddFailure("replacements", "Each replacement requires original and desired identities.");
                    return;
                }
                IReadOnlyList<PreviewResourceSelection>? selections = body switch
                {
                    AdoptRepositoryPreviewBody adoption => adoption.Resources,
                    RemoveRepositoryPreviewBody removal => removal.Resources,
                    ReplaceRepositoryPreviewBody replacement => replacement.Replacements?.Select(item => item.Original)
                        .ToArray(),
                    RelocateRepositoryPreviewBody or DetachRepositoryPreviewBody or ResourceChangesPreviewBody => []
                };
                if (selections is not null && selections.Any(item => item is null))
                {
                    context.AddFailure("resources", "Each selector requires a resource identity.");
                    return;
                }
                if (body is AdoptRepositoryPreviewBody or RemoveRepositoryPreviewBody or ReplaceRepositoryPreviewBody &&
                    (selections is null || selections.Count is < 1 or > 256 ||
                        selections.Select(item => (item.Kind,
                            item.ResourceId)).Distinct().Count() != selections.Count ||
                        selections.Select(item => (item.Kind, item.Name)).Distinct().Count() != selections.Count))
                    context.AddFailure("resources", "Select one to 256 distinct resource identities.");
                if (selections is not null && selections.Any(item => !DeclarationNames.Valid(item.Name) ||
                        string.IsNullOrWhiteSpace(item.Kind) || !ObjectId.TryParse(item.ResourceId, out _)))
                    context.AddFailure("resources", "Each selector requires a kind, technical name and resource ID.");
                if (body is ReplaceRepositoryPreviewBody replacements && replacements.Replacements is not null &&
                    (replacements.Replacements.Select(item => item.Desired).Distinct().Count() !=
                        replacements.Replacements.Count ||
                    replacements.Replacements.Any(item => item.Desired is null ||
                        item.Original.Kind != item.Desired.Kind || !DeclarationNames.Valid(item.Desired.Name))))
                    context.AddFailure("replacements", "Replacement must select a same-kind desired address.");
                if (body is RelocateRepositoryPreviewBody relocation &&
                    (relocation.Destination is null || string.IsNullOrWhiteSpace(relocation.Destination.Provider) ||
                        !MongoDB.Bson.ObjectId.TryParse(relocation.Destination.ConnectionId, out _) ||
                        string.IsNullOrWhiteSpace(relocation.Destination.ExternalRepositoryId) ||
                        string.IsNullOrWhiteSpace(relocation.Destination.Branch) ||
                        !RepositoryManifestDirectory.IsValid(relocation.Destination.ManifestDirectory)))
                    context.AddFailure("destination", "A complete safe repository location is required.");
            });
        });
    }
}

public sealed class CreateLifecyclePreviewHandler(GitLifecyclePreviews previews)
    : IMediatorOutcomeHandler<CreateLifecyclePreviewRequest, CreateLifecyclePreviewOutcome>
{
    public async Task<CreateLifecyclePreviewOutcome> HandleAsync(CreateLifecyclePreviewRequest request,
        CancellationToken cancellationToken = default)
    {
        var intent = request.Body switch
        {
            AdoptRepositoryPreviewBody adoption => (GitPreviewIntent)new GitPreviewAdoption(
                adoption.Resources.Select(Identity).ToImmutableArray()),
            RemoveRepositoryPreviewBody removal => new GitPreviewRemoval(removal.Resources.Select(Identity)
                .ToImmutableArray()),
            ReplaceRepositoryPreviewBody replacement => new GitPreviewReplacement(replacement.Replacements.Select(
                item => new GitReplacementSelection(Identity(item.Original), item.Desired.Kind, item.Desired.Name))
                .ToImmutableArray()),
            RelocateRepositoryPreviewBody relocation => new GitPreviewRelocateSource(new(
                relocation.Destination.Provider, ObjectId.Parse(relocation.Destination.ConnectionId).ToString(),
                relocation.Destination.ExternalRepositoryId,
                relocation.Destination.Branch, relocation.Destination.ManifestDirectory)),
            DetachRepositoryPreviewBody => new GitPreviewDetachSource(),
            ResourceChangesPreviewBody changes => new GitPreviewResourceChanges(changes.Removal)
        };
        var result = await previews.CreateAsync(new(new(ObjectId.Parse(request.TenantId).ToString(),
            ObjectId.Parse(request.Id).ToString()), request.Body.ExpectedRevision, intent), cancellationToken);
        return result switch
        {
            GitOperationPreview preview => LifecyclePreviewViews.View(preview),
            GitSourceNotFound missing => missing,
            GitSourceRevisionConflict stale => stale,
            GitSourceBusy busy => busy,
            GitPreviewReadFailed failed => failed,
            GitPreviewRejected blocked => blocked
        };
    }

    private static GitResourceIdentity Identity(PreviewResourceSelection selected) =>
        new(selected.Kind, selected.Name, ObjectId.Parse(selected.ResourceId).ToString());
}

public static class CreateLifecyclePreviewEndpoint
{
    public static IEndpointRouteBuilder MapCreateLifecyclePreviewEndpoint(this IEndpointRouteBuilder app)
    {
        app.MapPost("/{Id}/previews", async (IMediator mediator, [AsParameters] CreateLifecyclePreviewRequest request,
                CancellationToken ct) => (await mediator.Send(request, ct)).ToHttpResult(outcome => outcome switch
            {
                LifecyclePreviewView preview => Results.Created(
                    $"/api/v1/gitops/sources/{request.Id}/previews/{preview.Id}", preview),
                GitSourceNotFound missing => RepositoryHttpMappings.Map(missing),
                GitSourceRevisionConflict stale => RepositoryHttpMappings.Map(stale),
                GitSourceBusy busy => RepositoryHttpMappings.Map(busy),
                GitPreviewReadFailed failed => LifecycleHttpMappings.Map(failed),
                GitPreviewRejected blocked => LifecycleHttpMappings.Map(blocked)
            })).WithName("CreateGitLifecyclePreview")
            .WithMetadata(new RequestSizeLimitAttribute(128 * 1024));
        return app;
    }
}
