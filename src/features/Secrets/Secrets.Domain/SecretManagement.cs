using MongoDB.Bson.Serialization.Attributes;

namespace Pole.Features.Secrets.Domain;

[BsonDiscriminator(RootClass = true)]
[BsonKnownTypes(typeof(UnmanagedSecret), typeof(ProviderManagedSecret))]
public abstract record SecretManagement;

public sealed record UnmanagedSecret : SecretManagement;

public enum KubernetesSecretType
{
    Opaque,
    BasicAuthentication,
}

public sealed record ProviderManagedSecret(
    string ResourceType,
    string ResourceId,
    string BindingId,
    KubernetesSecretType KubernetesType
) : SecretManagement;
