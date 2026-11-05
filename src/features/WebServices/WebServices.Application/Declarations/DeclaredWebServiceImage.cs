using Pole.Features.WebServices.Application.Mongo;
using Pole.Features.WebServices.Application.Lifecycle;
using Pole.Features.WebServices.Application.Hostnames;
using Pole.Features.WebServices.Application.Configuration;
using Pole.Features.WebServices.Application;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Pole.Features.WebServices.Application.Declarations;

[JsonConverter(typeof(DeclaredWebServiceImageConverter))]
public closed record DeclaredWebServiceImage;

public sealed record DeclaredContainerImage(
    string Server,
    string Name,
    string Tag) : DeclaredWebServiceImage;

public sealed record DeclaredSourceBuildImage(string SourceBuildConfiguration) : DeclaredWebServiceImage;

internal sealed class DeclaredWebServiceImageConverter : JsonConverter<DeclaredWebServiceImage>
{
    public override DeclaredWebServiceImage Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options)
    {
        using var document = JsonDocument.ParseValue(ref reader);
        var value = document.RootElement;
        if (value.ValueKind != JsonValueKind.Object)
            throw new JsonException("An image object is required.");
        var names = value.EnumerateObject().Select(item => item.Name).ToArray();
        if (names.SequenceEqual(["sourceBuildConfiguration"]))
            return new DeclaredSourceBuildImage(String("sourceBuildConfiguration"));
        if (names.Length != 3 || !names.Order(StringComparer.Ordinal).SequenceEqual(["name", "server", "tag"]))
            throw new JsonException("Specify a concrete image or a build configuration, without mixed fields.");
        return new DeclaredContainerImage(String("server"), String("name"), String("tag"));

        string String(string name) => value.GetProperty(name).ValueKind == JsonValueKind.String
            ? value.GetProperty(name).GetString()! : throw new JsonException("Image fields must be strings.");
    }

    public override void Write(Utf8JsonWriter writer, DeclaredWebServiceImage value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        switch (value)
        {
            case DeclaredContainerImage image:
                writer.WriteString("server", image.Server);
                writer.WriteString("name", image.Name);
                writer.WriteString("tag", image.Tag);
                break;
            case DeclaredSourceBuildImage build:
                writer.WriteString("sourceBuildConfiguration", build.SourceBuildConfiguration);
                break;
        }
        writer.WriteEndObject();
    }
}
