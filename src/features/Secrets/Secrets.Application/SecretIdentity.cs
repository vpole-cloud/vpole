using System.Collections.Immutable;
using Pole.Features.Secrets.Domain;
using Pole.Workloads;

namespace Pole.Features.Secrets.Application;

internal static class SecretIdentity
{
    // Public routes validate IDs first. Trusted cleanup events can contain non-ObjectId
    // provider IDs; retain those as nonmatching identities rather than failing delivery.
    internal static string ObjectId(string value) =>
        MongoDB.Bson.ObjectId.TryParse(value, out var id) ? id.ToString() : value;

    internal static SecretBinding Binding(SecretBinding binding) => binding with
    {
        Id = ObjectId(binding.Id),
        Consumer = new SecretConsumerIdentity(
            ResourceTypeId.Create(binding.Consumer.ResourceType).Value,
            ObjectId(binding.Consumer.ResourceId)),
        Selection = binding.Selection switch
        {
            AllSecretDeployments => new AllSecretDeployments(),
            SelectedSecretDeployments selected => new SelectedSecretDeployments(
                selected.DeploymentIds.Select(ObjectId).ToImmutableArray()),
            _ => throw new InvalidOperationException("Unknown deployment selection."),
        },
    };
}
