using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;
using System.Collections.Immutable;

namespace Pole.Features.DeploymentTargets.Domain;

public enum DeploymentTargetConnectorEnrollmentKind
{
    Initial,
    Rotation
}

/// <summary>The digest is a verifier for a 256-bit, single-use capability; plaintext is never persisted.</summary>
public sealed record DeploymentTargetConnectorEnrollment(
    [property: BsonId]
    [property: BsonRepresentation(BsonType.ObjectId)]
    string Id,
    string TargetId,
    string ConnectorId,
    DeploymentTargetConnectorEnrollmentKind Kind,
    long Generation,
    string TokenDigestSha256,
    DateTime IssuedAtUtc,
    DateTime ExpiresAtUtc,
    DateTime? ConsumedAtUtc)
{
    public static DeploymentTargetConnectorEnrollment Issue(
        string targetId,
        string connectorId,
        DeploymentTargetConnectorEnrollmentKind kind,
        long generation,
        string tokenDigestSha256,
        DateTime issuedAtUtc,
        TimeSpan lifetime)
    {
        if (!ObjectId.TryParse(targetId, out var parsedTargetId) ||
            !ObjectId.TryParse(connectorId, out var parsedConnectorId))
            throw new ArgumentException("A connector enrollment requires valid target and connector IDs.");
        if (generation < 1 || tokenDigestSha256.Length != 64 ||
            !tokenDigestSha256.All(Uri.IsHexDigit))
            throw new ArgumentException("A connector enrollment has invalid identity metadata.");
        if (lifetime <= TimeSpan.Zero || lifetime > TimeSpan.FromHours(1))
            throw new ArgumentOutOfRangeException(nameof(lifetime));
        if (issuedAtUtc.Kind != DateTimeKind.Utc)
            throw new ArgumentException("Enrollment time must be UTC.", nameof(issuedAtUtc));

        return new DeploymentTargetConnectorEnrollment(
            ObjectId.GenerateNewId().ToString(),
            parsedTargetId.ToString(),
            parsedConnectorId.ToString(),
            kind,
            generation,
            tokenDigestSha256,
            issuedAtUtc,
            issuedAtUtc.Add(lifetime),
            null);
    }
}

public sealed record DeploymentTargetConnectorObservation(
    string Version,
    string Health,
    ImmutableArray<string> Capabilities,
    DateTime LastContactAtUtc)
{
    public static readonly TimeSpan ContactFreshness = TimeSpan.FromSeconds(10);

    public bool IsFreshAt(DateTime nowUtc) =>
        nowUtc - LastContactAtUtc < ContactFreshness;
}

[BsonDiscriminator(RootClass = true)]
[BsonKnownTypes(
    typeof(ActiveDeploymentTargetConnectorState),
    typeof(RevokedDeploymentTargetConnectorState))]
public closed record DeploymentTargetConnectorState;

[BsonDiscriminator(nameof(ActiveDeploymentTargetConnectorState))]
public sealed record ActiveDeploymentTargetConnectorState : DeploymentTargetConnectorState;

[BsonDiscriminator(nameof(RevokedDeploymentTargetConnectorState))]
public sealed record RevokedDeploymentTargetConnectorState(DateTime RevokedAtUtc)
    : DeploymentTargetConnectorState;

public sealed record DeploymentTargetConnector(
    [property: BsonId]
    [property: BsonRepresentation(BsonType.ObjectId)]
    string Id,
    string TargetId,
    string CertificateFingerprintSha256,
    string PublicKeySpkiSha256,
    DateTime CertificateNotAfterUtc,
    long Generation,
    long Revision,
    DateTime CreatedAtUtc,
    DateTime UpdatedAtUtc,
    DeploymentTargetConnectorState State,
    DeploymentTargetConnectorObservation? Observation)
{
    public bool IsActiveAt(DateTime nowUtc) =>
        State is ActiveDeploymentTargetConnectorState && CertificateNotAfterUtc > nowUtc;

    public DeploymentTargetConnector Revoke(DateTime nowUtc)
    {
        if (State is RevokedDeploymentTargetConnectorState)
            return this;
        if (nowUtc.Kind != DateTimeKind.Utc)
            throw new ArgumentException("Revocation time must be UTC.", nameof(nowUtc));
        return this with
        {
            State = new RevokedDeploymentTargetConnectorState(nowUtc),
            Revision = checked(Revision + 1),
            UpdatedAtUtc = nowUtc
        };
    }

    public static DeploymentTargetConnector Enroll(
        DeploymentTargetConnectorEnrollment enrollment,
        string certificateFingerprintSha256,
        string publicKeySpkiSha256,
        DateTime certificateNotAfterUtc,
        DateTime nowUtc)
    {
        if (enrollment.Kind != DeploymentTargetConnectorEnrollmentKind.Initial ||
            enrollment.Generation != 1 || enrollment.ConsumedAtUtc is not null)
            throw new ArgumentException("The enrollment is not an unconsumed initial enrollment.", nameof(enrollment));
        if (certificateFingerprintSha256.Length != 64 ||
            publicKeySpkiSha256.Length != 64 ||
            !certificateFingerprintSha256.All(Uri.IsHexDigit) ||
            !publicKeySpkiSha256.All(Uri.IsHexDigit) ||
            certificateNotAfterUtc.Kind != DateTimeKind.Utc ||
            nowUtc.Kind != DateTimeKind.Utc ||
            certificateNotAfterUtc <= nowUtc)
            throw new ArgumentException("The connector certificate metadata is invalid.");

        return new DeploymentTargetConnector(
            enrollment.ConnectorId,
            enrollment.TargetId,
            certificateFingerprintSha256,
            publicKeySpkiSha256,
            certificateNotAfterUtc,
            1,
            1,
            nowUtc,
            nowUtc,
            new ActiveDeploymentTargetConnectorState(),
            null);
    }
}
