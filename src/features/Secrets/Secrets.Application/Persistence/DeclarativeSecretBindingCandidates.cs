using MongoDB.Driver;
using Pole.Features.GitOps.Contracts;
using Pole.Features.Secrets.Domain;

namespace Pole.Features.Secrets.Application.Persistence;

internal sealed partial class DeclarativeSecretBindingProvider
{
    public async Task<DeclarationAdoptionCandidatePage> FindAdoptionCandidatesAsync(
        FindDeclarationAdoptionCandidatesRequest request,
        IClientSessionHandle session,
        CancellationToken cancellationToken)
    {
        var related = request.References.Related;
        var consumer = request.References.Consumer;
        if (related.Address.Kind != "Secret" || !consumers.TryGetValue(consumer.Address.Kind, out var capability))
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
