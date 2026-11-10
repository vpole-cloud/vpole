using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace Pole.Features.WebServices.Domain;

[BsonDiscriminator(RootClass = true)]
[BsonKnownTypes(
    typeof(ImageInfo),
    typeof(UnboundSourceBuildImage),
    typeof(PendingSourceBuildImage),
    typeof(DeliveredSourceBuildImage))]
public closed record WebServiceImage
{
    [BsonIgnore]
    public ImageInfo? ResolvedImage =>
        this switch
        {
            ImageInfo direct => direct,
            DeliveredSourceBuildImage delivered => delivered.Image,
            UnboundSourceBuildImage or PendingSourceBuildImage => null,
        };

    [BsonIgnore]
    public string? SourceBuildConfigurationId =>
        this switch
        {
            PendingSourceBuildImage pending => pending.ConfigurationId,
            DeliveredSourceBuildImage delivered => delivered.ConfigurationId,
            ImageInfo or UnboundSourceBuildImage => null,
        };

    [BsonIgnore]
    public string? ResolvedImageReference =>
        this switch
        {
            ImageInfo direct => direct.FullImageName,
            DeliveredSourceBuildImage delivered =>
                $"{delivered.Image.Server}/{delivered.Image.Name}@{delivered.Digest}",
            UnboundSourceBuildImage or PendingSourceBuildImage => null,
        };
}

public sealed record ImageInfo(
    string Server,
    string Name,
    string Tag,
    string? SecretName
) : WebServiceImage
{
    [BsonIgnore] public string FullImageName => $"{Server}/{Name}:{Tag}";
}

public sealed record UnboundSourceBuildImage : WebServiceImage;

public sealed record PendingSourceBuildImage(
    string ConfigurationId
) : WebServiceImage;

public sealed record DeliveredSourceBuildImage(
    string ConfigurationId,
    ImageInfo Image,
    string Digest,
    string JobId,
    long Generation
) : WebServiceImage
{
    public static DeliveredSourceBuildImage Create(
        string configurationId,
        ImageInfo image,
        string digest,
        string jobId,
        long generation)
    {
        var result = new DeliveredSourceBuildImage(configurationId, image, digest, jobId, generation);
        WebServiceImageFactory.Validate(result);
        return result;
    }
}

public static class WebServiceImageFactory
{
    public static PendingSourceBuildImage Pending(string configurationId)
    {
        var result = new PendingSourceBuildImage(configurationId);
        Validate(result);
        return result;
    }

    internal static void Validate(WebServiceImage image)
    {
        ArgumentNullException.ThrowIfNull(image);
        if (image.SourceBuildConfigurationId is { } configurationId && !ObjectId.TryParse(configurationId, out _))
            throw new ArgumentException("A build configuration ObjectId is required.", nameof(image));
        if (image is DeliveredSourceBuildImage delivered)
        {
            ArgumentNullException.ThrowIfNull(delivered.Image);
            if (!ObjectId.TryParse(delivered.JobId, out _) || delivered.Generation <= 0)
                throw new ArgumentException(
                    "A build job identity and positive generation are required.",
                    nameof(image));
            if (delivered.Digest.Length != 71 || !delivered.Digest.StartsWith("sha256:", StringComparison.Ordinal) ||
                delivered.Digest.AsSpan(7).ContainsAnyExcept("0123456789abcdef"))
                throw new ArgumentException("An immutable SHA256 artifact digest is required.", nameof(image));
        }
    }
}
