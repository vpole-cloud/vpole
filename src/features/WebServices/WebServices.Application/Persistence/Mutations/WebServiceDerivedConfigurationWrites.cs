using MongoDB.Driver;
using Pole.Features.Domains.Contracts;
using Pole.Features.Secrets.Contracts;
using Pole.Features.SourceBuilds.Contracts;
using Pole.Features.WebServices.Domain;
using Pole.Features.WebServices.Events;

namespace Pole.Features.WebServices.Application.Persistence.Mutations;

internal sealed record WebServiceHostnameAttachmentWrite(
    WebService Current,
    WebService Replacement,
    WebServiceUpdatedEvent Event,
    WebServiceHostnameAuthorization Authorization);

internal closed record WebServiceHostnameAuthorization;

internal sealed record AttachedWebServiceHostnameBinding(string BindingId) : WebServiceHostnameAuthorization;

internal sealed record RemovedWebServiceHostnameBinding(
    string BindingId,
    HostnameAttachmentSnapshot Previous) : WebServiceHostnameAuthorization;

internal delegate Task<PersistWebServiceUpdateResult> PersistWebServiceHostnameAttachment(
    WebServiceHostnameAttachmentWrite request,
    IClientSessionHandle session,
    CancellationToken cancellationToken);

public sealed record WebServiceArtifactPromotionWrite(
    WebService Current,
    WebService Replacement,
    WebServiceUpdatedEvent Event,
    SourceBuildPromotionRequest Promotion);

public delegate Task<PersistWebServiceUpdateResult> PersistWebServiceArtifactPromotion(
    WebServiceArtifactPromotionWrite request,
    IClientSessionHandle session,
    CancellationToken cancellationToken);

internal sealed partial class MongoPersistUpdatedWebService
{
    public async Task<PersistWebServiceUpdateResult> PersistHostnameAsync(
        WebServiceHostnameAttachmentWrite request,
        IClientSessionHandle session,
        CancellationToken cancellationToken)
    {
        var current = request.Current;
        var next = request.Replacement;
        if (!PreservesResourceMetadata(current, next) ||
            current.Lifecycle is not (ActiveWebServiceLifecycle or AwaitingFirstArtifactWebServiceLifecycle) ||
            !current.Spec.HasSameConfiguration(next.Spec) || current.Exposure != next.Exposure ||
            !current.Tags.SetEquals(next.Tags) || current.Lifecycle != next.Lifecycle ||
            next.Revision != current.Revision + 1 || next.ActiveRevision != current.ActiveRevision + 1)
            return Denied(current);

        var authorized = request.Authorization switch
        {
            AttachedWebServiceHostnameBinding attached => await IsAttachedAsync(attached.BindingId),
            RemovedWebServiceHostnameBinding removed => await IsRemovedAsync(removed)
        };
        if (!authorized)
            return Denied(current);

        return await WriteAuthorizedAsync(new(current, next, request.Event,
            PreviousSecretReferenceRetention.KeepUntilRolloutConfirmed), session, cancellationToken);

        async Task<bool> IsAttachedAsync(string bindingId)
        {
            var binding = await loadHostnameBinding(new(current.TenantId, bindingId), session, cancellationToken);
            return binding is not null && binding.Binding.Kind == HostnameBindingKinds.Custom &&
                binding.Binding.ResourceId == current.Id &&
                binding.Binding.ResourceType == Common.WebServiceResourceDeployment.ResourceType.Value;
        }

        async Task<bool> IsRemovedAsync(RemovedWebServiceHostnameBinding removed)
        {
            var previous = removed.Previous;
            return previous.TenantId == current.TenantId && previous.ResourceId == current.Id &&
                previous.ResourceType == Common.WebServiceResourceDeployment.ResourceType &&
                previous.CurrentHostnames.Contains(previous.Hostname, StringComparer.Ordinal) &&
                previous.DocumentVersions.Any(item => item.DocumentId == $"WebService/{current.Id}" &&
                    item.Version == current.Revision) &&
                await loadHostnameBinding(new(current.TenantId, removed.BindingId), session, cancellationToken) is null;
        }
    }

    public async Task<PersistWebServiceUpdateResult> PersistArtifactAsync(
        WebServiceArtifactPromotionWrite request,
        IClientSessionHandle session,
        CancellationToken cancellationToken)
    {
        var current = request.Current;
        var next = request.Replacement;
        var promotion = request.Promotion;
        if (!PreservesResourceMetadata(current, next) ||
            current.Lifecycle is not (ActiveWebServiceLifecycle or AwaitingFirstArtifactWebServiceLifecycle) ||
            current.TenantId != promotion.TenantId || current.Id != promotion.WebServiceId ||
            current.Spec.Image.SourceBuildConfigurationId != promotion.Authority.ConfigurationId ||
            next.Spec.Image is not DeliveredSourceBuildImage artifact ||
            artifact.ConfigurationId != promotion.Authority.ConfigurationId || artifact.JobId != promotion.BuildJobId ||
            artifact.Generation != promotion.Authority.Generation || artifact.Digest != promotion.ImageDigest ||
            !(current.Spec with { Image = artifact }).HasSameConfiguration(next.Spec) ||
            current.Exposure != next.Exposure || !current.Tags.SetEquals(next.Tags) ||
            next.Lifecycle is not ActiveWebServiceLifecycle || next.Revision != current.Revision + 1 ||
            next.ActiveRevision != current.ActiveRevision + 1)
            return Denied(current);

        if (await beginPromotion(promotion, session, cancellationToken) is not SourceBuildPromotionAuthorized)
            return new WebServiceUpdateConflict(current.Id);

        return await WriteAuthorizedAsync(new(current, next, request.Event,
            PreviousSecretReferenceRetention.KeepUntilRolloutConfirmed), session, cancellationToken);
    }

    private static bool PreservesResourceMetadata(WebService current, WebService replacement) =>
        (replacement with
        {
            Spec = current.Spec,
            Exposure = current.Exposure,
            Tags = current.Tags,
            Lifecycle = current.Lifecycle,
            Revision = current.Revision,
            ActiveRevision = current.ActiveRevision
        }) == current;

    private static WebServiceUpdateConflict Denied(WebService current) =>
        new(current.Id, WebServiceUpdateConflictReason.GitOwnedConfiguration);
}
