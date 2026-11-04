using System.Text.Json.Serialization;
using MassTransit;
using Pole.Features.Events.Abstractions;

namespace Pole.Features.Secrets.Events;

[EntityName("pole.secrets/v1/secret-changed")]
public sealed record SecretChangedEvent : IPoleEvent
{
    public SecretChangedEvent(
        string SecretId,
        long Version,
        long Revision,
        SecretChange Change)
    {
        this.SecretId = SecretId;
        this.Version = Version;
        this.Revision = Revision;
        this.Change = Change;
    }

    public string SecretId { get; init; }

    public long Version { get; init; }

    public long Revision { get; init; }

    public SecretChange Change { get; init; }

    public string? Name { get; init; }
    public EventCore EventCore
    {
        get; init;
    } = EventCore.Create();
}

[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(SecretCreated), "created")]
[JsonDerivedType(typeof(SecretRotated), "rotated")]
[JsonDerivedType(typeof(SecretMetadataUpdated), "metadata-updated")]
[JsonDerivedType(typeof(SecretDeletedChange), "deleted")]
[JsonDerivedType(typeof(SecretBindingAdded), "binding-added")]
[JsonDerivedType(typeof(SecretBindingRemoved), "binding-removed")]
public abstract record SecretChange;

public sealed record SecretCreated : SecretChange;

public sealed record SecretRotated : SecretChange;

public sealed record SecretMetadataUpdated : SecretChange;

public sealed record SecretDeletedChange : SecretChange;

public sealed record SecretBindingAdded(string BindingId) : SecretChange;

public sealed record SecretBindingRemoved(string BindingId) : SecretChange;
