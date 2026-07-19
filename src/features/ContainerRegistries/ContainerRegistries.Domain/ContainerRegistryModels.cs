using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;
using Pole.Encryption;

namespace Pole.Features.ContainerRegistries.Domain;

public enum ContainerRegistryProvider
{
    Harbor
}

public sealed record RegistryProject(
    [property: BsonId]
    [property: BsonRepresentation(BsonType.ObjectId)]
    string Id,
    string TenantId,
    [property: BsonRepresentation(BsonType.String)]
    ContainerRegistryProvider Provider,
    string Name,
    RegistryProjectState State,
    string CreatedByUserId,
    DateTime CreatedAtUtc,
    DateTime UpdatedAtUtc);

[BsonDiscriminator(RootClass = true)]
[BsonKnownTypes(
    typeof(ProvisioningRegistryProject),
    typeof(ReadyRegistryProject),
    typeof(FailedRegistryProjectProvisioning))]
public abstract record RegistryProjectState;

public sealed record ProvisioningRegistryProject(DateTime StartedAtUtc) : RegistryProjectState;

public sealed record ReadyRegistryProject(
    string ExternalProjectId,
    string BuildCredentialId,
    string PullCredentialId,
    DateTime ProvisionedAtUtc) : RegistryProjectState;

public sealed record FailedRegistryProjectProvisioning(
    string Code,
    string Message,
    DateTime FailedAtUtc) : RegistryProjectState;

public static class RegistryProjectFactory
{
    public sealed record CreateOptions(string Id, DateTime CreatedAtUtc);

    public static CreateOptions DefaultOptions =>
        new(ObjectId.GenerateNewId().ToString(), DateTime.UtcNow);

    public static RegistryProject Create(
        string tenantId,
        ContainerRegistryProvider provider,
        string name,
        string createdByUserId,
        CreateOptions options) =>
        new(
            options.Id,
            tenantId,
            provider,
            name,
            new ProvisioningRegistryProject(options.CreatedAtUtc),
            createdByUserId,
            options.CreatedAtUtc,
            options.CreatedAtUtc);
}

public enum RegistryCredentialPurpose
{
    BuildPushPull,
    RuntimePull
}

public sealed record RegistryCredential(
    [property: BsonId]
    [property: BsonRepresentation(BsonType.ObjectId)]
    string Id,
    string TenantId,
    string RegistryProjectId,
    [property: BsonRepresentation(BsonType.String)]
    RegistryCredentialPurpose Purpose,
    string ExternalRobotId,
    string Username,
    long SecretVersion,
    EncryptedValue EncryptedSecret,
    DateTime CreatedAtUtc,
    DateTime? ExpiresAtUtc);

public static class RegistryCredentialFactory
{
    public sealed record CreateOptions(string Id, DateTime CreatedAtUtc);

    public static CreateOptions DefaultOptions =>
        new(ObjectId.GenerateNewId().ToString(), DateTime.UtcNow);

    public static RegistryCredential Create(
        string tenantId,
        string registryProjectId,
        RegistryCredentialPurpose purpose,
        string externalRobotId,
        string username,
        long secretVersion,
        EncryptedValue encryptedSecret,
        DateTime? expiresAtUtc,
        CreateOptions options) =>
        new(
            options.Id,
            tenantId,
            registryProjectId,
            purpose,
            externalRobotId,
            username,
            secretVersion,
            encryptedSecret,
            options.CreatedAtUtc,
            expiresAtUtc);
}
