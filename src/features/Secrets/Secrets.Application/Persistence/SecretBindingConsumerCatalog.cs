using Pole.Features.Secrets.Contracts;

namespace Pole.Features.Secrets.Application.Persistence;

internal sealed class SecretBindingConsumerCatalog(IEnumerable<SecretDeclarationConsumer> capabilities)
{
    private readonly IReadOnlyDictionary<string, SecretDeclarationConsumer> consumers =
        capabilities.ToDictionary(item => item.Kind, StringComparer.Ordinal);

    public IEnumerable<SecretDeclarationConsumer> All => consumers.Values;

    public bool TryGet(string kind, out SecretDeclarationConsumer consumer) =>
        consumers.TryGetValue(kind, out consumer!);

    public SecretDeclarationConsumer Get(string kind) => consumers[kind];
}
