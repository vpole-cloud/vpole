using System.Buffers;
using System.Buffers.Binary;
using System.Text;

namespace Pole.Encryption;

internal static class AssociatedDataEncoder
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public static byte[] ForPayload(
        int formatVersion,
        string contentAlgorithm,
        int contentAlgorithmVersion,
        EnvelopeEncryptionContext context)
    {
        var writer = new ArrayBufferWriter<byte>();
        WriteString(writer, "Pole.EnvelopeEncryption.Payload");
        WriteInt32(writer, formatVersion);
        WriteString(writer, contentAlgorithm);
        WriteInt32(writer, contentAlgorithmVersion);
        WriteContext(writer, context);
        return writer.WrittenSpan.ToArray();
    }

    public static byte[] ForWrappedKey(
        int formatVersion,
        string providerId,
        KeyEncryptionKeyIdentity key,
        string keyWrapAlgorithm,
        int keyWrapAlgorithmVersion,
        string contentAlgorithm,
        int contentAlgorithmVersion,
        EnvelopeEncryptionContext context)
    {
        var writer = new ArrayBufferWriter<byte>();
        WriteString(writer, "Pole.EnvelopeEncryption.WrappedDataEncryptionKey");
        WriteInt32(writer, formatVersion);
        WriteString(writer, providerId);
        WriteString(writer, key.Id);
        WriteInt32(writer, key.Version);
        WriteString(writer, keyWrapAlgorithm);
        WriteInt32(writer, keyWrapAlgorithmVersion);
        WriteString(writer, contentAlgorithm);
        WriteInt32(writer, contentAlgorithmVersion);
        WriteContext(writer, context);
        return writer.WrittenSpan.ToArray();
    }

    private static void WriteContext(ArrayBufferWriter<byte> writer, EnvelopeEncryptionContext context)
    {
        WriteInt32(writer, (int)context.ScopeKind);
        WriteString(writer, context.ScopeId);
        WriteString(writer, context.ObjectType);
        WriteString(writer, context.ObjectId);
        WriteString(writer, context.ValueName);
        WriteInt64(writer, context.ValueVersion);
    }

    private static void WriteString(ArrayBufferWriter<byte> writer, string value)
    {
        var byteCount = StrictUtf8.GetByteCount(value);
        WriteInt32(writer, byteCount);
        StrictUtf8.GetBytes(value, writer.GetSpan(byteCount));
        writer.Advance(byteCount);
    }

    private static void WriteInt32(ArrayBufferWriter<byte> writer, int value)
    {
        BinaryPrimitives.WriteInt32BigEndian(writer.GetSpan(sizeof(int)), value);
        writer.Advance(sizeof(int));
    }

    private static void WriteInt64(ArrayBufferWriter<byte> writer, long value)
    {
        BinaryPrimitives.WriteInt64BigEndian(writer.GetSpan(sizeof(long)), value);
        writer.Advance(sizeof(long));
    }
}
