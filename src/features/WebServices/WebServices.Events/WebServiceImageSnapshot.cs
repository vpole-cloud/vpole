using System.Text.Json.Serialization;

namespace Pole.Features.WebServices.Events;

[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(WebServiceImageInfoSnapshot), "Direct")]
[JsonDerivedType(typeof(UnboundSourceBuildImageSnapshot), "UnboundSourceBuild")]
[JsonDerivedType(typeof(PendingSourceBuildImageSnapshot), "PendingSourceBuild")]
[JsonDerivedType(typeof(DeliveredSourceBuildImageSnapshot), "DeliveredSourceBuild")]
public closed record WebServiceImageSnapshot
{
    [JsonIgnore]
    public string? ResolvedImageReference => this switch
    {
        WebServiceImageInfoSnapshot direct => direct.FullImageName,
        DeliveredSourceBuildImageSnapshot delivered =>
            $"{delivered.Image.Server}/{delivered.Image.Name}@{delivered.Digest}",
        UnboundSourceBuildImageSnapshot or PendingSourceBuildImageSnapshot => null
    };
}

public sealed record UnboundSourceBuildImageSnapshot : WebServiceImageSnapshot;

public sealed record PendingSourceBuildImageSnapshot(string ConfigurationId) : WebServiceImageSnapshot;

public sealed record DeliveredSourceBuildImageSnapshot(
    string ConfigurationId,
    WebServiceImageInfoSnapshot Image,
    string Digest,
    string JobId,
    long Generation) : WebServiceImageSnapshot;

public enum WebServiceLifecycleState
{
    Prepared,
    AwaitingFirstArtifact,
    Active,
    Deleting
}
