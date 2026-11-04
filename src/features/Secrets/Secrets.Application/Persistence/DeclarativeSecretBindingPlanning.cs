using MongoDB.Driver;
using Pole.Features.GitOps.Contracts;
using Pole.Features.Secrets.Domain;

namespace Pole.Features.Secrets.Application.Persistence;

internal sealed partial class DeclarativeSecretBindingProvider
{
    public async Task<ReadCurrentDeclarationOutcome> ReadCurrentAsync(
        ReadCurrentDeclarationRequest request,
        IClientSessionHandle session,
        CancellationToken cancellationToken)
    {
        var secret = await secrets.Find(session,
            SecretFilters.DeclarationBindingName(request.TenantId, request.Address.Name))
            .FirstOrDefaultAsync(cancellationToken);
        if (secret is null)
            return new CurrentDeclarationMissing();

        var binding = secret.Bindings.Single(item => item.DeclarationName == request.Address.Name);
        if (binding.Selection is not AllSecretDeployments || binding.Consumer.ResourceType != ConsumerType.Value)
            return new DeclarationReadBlocked(
                new SecretBindingDeclarationRejected(SecretBindingDeclarationError.ImmutableBindingChange));

        return new CurrentDeclaration(binding.Id, new([new($"Secret/{secret.Id}", secret.Revision)]),
            Project(secret.Id, binding.Consumer.ResourceId, binding.KeyMappings),
            DeclarationConfigurationState.Configured);
    }

    public async Task<ProjectDesiredDeclarationOutcome> ProjectDesiredAsync(
        ProjectDesiredDeclarationRequest request,
        IClientSessionHandle session,
        CancellationToken cancellationToken)
    {
        var spec = DeclarationJson.Read<SecretBindingDeclaration>(NormalizeAndValidate(request.Declaration.Spec));
        var secretAddress = new ResourceAddress("Secret", spec.Secret);
        var secretId = await git.ResolveAsync(new(request.TenantId, secretAddress), session, cancellationToken);
        if (secretId is null)
            return new DeclarationReadBlocked(new DeclarationReferenceUnavailable(secretAddress));

        var consumerAddress = new ResourceAddress("WebService", spec.WebService);
        var consumerId = await git.ResolveAsync(new(request.TenantId, consumerAddress), session, cancellationToken);
        if (consumerId is null)
            return new DeclarationReadBlocked(new DeclarationReferenceUnavailable(consumerAddress));

        return Project(secretId, consumerId, spec.KeyMappings);
    }

    private static DeclarationProjection Project(
        string secretId,
        string consumerId,
        IEnumerable<KeyValuePair<string, string>> keyMappings) => new(
            DeclarationJson.Write(new { secretId, consumerId }),
            DeclarationJson.Write(new
            {
                keyMappings = keyMappings.OrderBy(item => item.Key, StringComparer.Ordinal)
                    .ToDictionary(item => item.Key, item => item.Value)
            }));
}
