using MongoDB.Driver;
using Pole.Features.GitOps.Contracts;
using Pole.Features.Secrets.Application;
using Pole.Features.Secrets.Application.Bindings;
using Pole.Features.Secrets.Application.Bootstrap;
using Pole.Features.Secrets.Application.Lifecycle;
using Pole.Features.Secrets.Application.Managed;
using Pole.Features.Secrets.Application.Mongo;
using Pole.Features.Secrets.Application.Projections;
using Pole.Features.Secrets.Application.References;
using Pole.Features.Secrets.Application.Secrets;
using Pole.Features.Secrets.Application.Values;
using Pole.Features.Secrets.Domain;

namespace Pole.Features.Secrets.Application.Declarations;

internal sealed class SecretBindingCandidates(
    IMongoCollection<Secret> secrets,
    SecretBindingConsumerCatalog consumers)
{
    public async Task<DeclarationAdoptionCandidatePage> FindAdoptionCandidatesAsync(
        FindDeclarationAdoptionCandidatesRequest request,
        IClientSessionHandle session,
        CancellationToken cancellationToken)
    {
        var related = request.References.Related;
        var consumer = request.References.Consumer;
        if (related.Address.Kind != "Secret" || !consumers.TryGet(consumer.Address.Kind, out var capability))
            return new([], false);
        var secret = await secrets.Find(session, SecretFilters.Identity(request.TenantId, related.ResourceId))
            .FirstOrDefaultAsync(cancellationToken);
        if (secret is null || secret.Name != related.Address.Name || secret.Management is ProviderManagedSecret)
            return new([], false);

        var bindings = secret.Bindings.Where(item => item.Consumer.ResourceType == capability.ResourceType.Value &&
                item.Consumer.ResourceId == consumer.ResourceId)
            .OrderBy(item => item.Id, StringComparer.Ordinal)
            .Skip((request.Page.Page - 1) * request.Page.PageSize).Take(request.Page.PageSize + 1).ToArray();
        return new(bindings.Take(request.Page.PageSize).Select(item => new DeclarationAdoptionCandidate(item.Id,
            item.DeclarationName, new(related, consumer, item.Usage.ToString(), item.Selection switch
            {
                AllSecretDeployments => new("All", []),
                SelectedSecretDeployments selected => new("Selected", selected.DeploymentIds),
                _ => throw new ArgumentOutOfRangeException(nameof(item.Selection))
            }))).ToArray(), bindings.Length > request.Page.PageSize);
    }
}
