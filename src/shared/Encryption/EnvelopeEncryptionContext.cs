using System.Text;

namespace Pole.Encryption;

public enum EnvelopeEncryptionScopeKind
{
    Tenant = 1,
    Platform = 2,
}

public sealed class EnvelopeEncryptionContext
{
    private const int MaximumNameBytes = 128;
    private const int MaximumIdentifierBytes = 512;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    private EnvelopeEncryptionContext(
        EnvelopeEncryptionScopeKind scopeKind,
        string scopeId,
        string objectType,
        string objectId,
        string valueName,
        long valueVersion)
    {
        ScopeKind = scopeKind;
        ScopeId = Required(scopeId, nameof(scopeId), MaximumIdentifierBytes);
        ObjectType = Required(objectType, nameof(objectType), MaximumNameBytes);
        ObjectId = Required(objectId, nameof(objectId), MaximumIdentifierBytes);
        ValueName = Required(valueName, nameof(valueName), MaximumNameBytes);
        if (valueVersion < 1)
            throw new ArgumentOutOfRangeException(
                nameof(valueVersion),
                "The protected value version must be positive.");
        ValueVersion = valueVersion;
    }

    internal EnvelopeEncryptionScopeKind ScopeKind { get; }
    internal string ScopeId { get; }
    internal string ObjectType { get; }
    internal string ObjectId { get; }
    internal string ValueName { get; }
    internal long ValueVersion { get; }

    public static EnvelopeEncryptionContext ForTenant(
        string tenantId,
        string objectType,
        string objectId,
        string valueName,
        long valueVersion) => new(
        EnvelopeEncryptionScopeKind.Tenant,
        tenantId,
        objectType,
        objectId,
        valueName,
        valueVersion);

    public static EnvelopeEncryptionContext ForPlatform(
        string platformScopeId,
        string objectType,
        string objectId,
        string valueName,
        long valueVersion) => new(
        EnvelopeEncryptionScopeKind.Platform,
        platformScopeId,
        objectType,
        objectId,
        valueName,
        valueVersion);

    public override string ToString() => $"EnvelopeEncryptionContext(scope={ScopeKind})";

    private static string Required(
        string value,
        string parameterName,
        int maximumBytes)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException("The encryption context value is required.", parameterName);

        try
        {
            if (StrictUtf8.GetByteCount(value) > maximumBytes)
                throw new ArgumentException("The encryption context value is too long.", parameterName);
        }
        catch (EncoderFallbackException exception)
        {
            throw new ArgumentException("The encryption context value is not valid UTF-8.", parameterName, exception);
        }

        return value;
    }
}
