using Pole.Features.Secrets.Application.Values;
using Pole.Features.Secrets.Application.Secrets;
using Pole.Features.Secrets.Application.References;
using Pole.Features.Secrets.Application.Projections;
using Pole.Features.Secrets.Application.Mongo;
using Pole.Features.Secrets.Application.Managed;
using Pole.Features.Secrets.Application.Lifecycle;
using Pole.Features.Secrets.Application.Declarations;
using Pole.Features.Secrets.Application.Bootstrap;
using Pole.Features.Secrets.Application;
using System.Text.Json;
using Pole.Features.GitOps.Contracts;
using Pole.Features.Secrets.Contracts;

namespace Pole.Features.Secrets.Application.Bindings;

internal sealed class SecretBindingConsumerCatalog(IEnumerable<SecretDeclarationConsumer> capabilities)
{
    private readonly IReadOnlyDictionary<string, SecretDeclarationConsumer> consumers =
        capabilities.ToDictionary(item => item.Kind, StringComparer.Ordinal);

    public IEnumerable<SecretDeclarationConsumer> All => consumers.Values;

    public bool TryGet(string kind, out SecretDeclarationConsumer consumer) =>
        consumers.TryGetValue(kind, out consumer!);

    public SecretDeclarationConsumer Get(string kind) => consumers[kind];

    public JsonElement NormalizeAndValidate(JsonElement spec)
    {
        var value = DeclarationJson.Read<SecretBindingDeclaration>(spec);
        DeclarationJson.Require(DeclarationNames.Valid(value.Secret) && value.Consumer is not null &&
                                DeclarationNames.Valid(value.Consumer.Name) &&
                                TryGet(value.Consumer.Kind, out var consumer) &&
                                consumer.Usage == value.Usage &&
                                value.KeyMappings is { Count: <= SecretKeyReferenceRules.MaximumKeys } &&
                                value.KeyMappings.All(pair => SecretKeyReferenceRules.ValidKey(pair.Key) &&
                                    SecretKeyReferenceRules.ValidKey(pair.Value)) &&
                                value.KeyMappings.Values.Distinct().Count() == value.KeyMappings.Count,
            DeclarationFieldPaths.Spec);
        return DeclarationJson.Write(value);
    }
}
