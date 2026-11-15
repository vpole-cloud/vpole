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

public enum ManagedSecretSharing
{
    Shareable,
    OwnerOnly,
}

public sealed record ProviderManagedSecret(
    string ResourceType,
    string ResourceId,
    string Purpose,
    string BindingId,
    KubernetesSecretType KubernetesType,
    ManagedSecretSharing Sharing
) : SecretManagement
{
    public static string RequirePurpose(string purpose)
    {
        if (string.IsNullOrWhiteSpace(purpose) || purpose.Length > 128 || purpose.Any(character =>
                !char.IsAsciiLetterOrDigit(character) && character is not ':' and not '.' and not '_' and not '-'))
            throw new ArgumentException(
                "Managed credential purpose must be a stable ASCII identifier of 1 to 128 characters.",
                nameof(purpose));
        return purpose;
    }
}
