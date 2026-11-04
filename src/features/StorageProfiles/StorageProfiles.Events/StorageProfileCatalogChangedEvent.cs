using MassTransit;
using Pole.Features.Events.Abstractions;

namespace Pole.Features.StorageProfiles.Events;

[EntityName("pole.storage-profiles/v1/storage-profile-catalog-changed")]
public sealed record StorageProfileCatalogChangedEvent(
    string StorageProfileId,
    long Revision,
    string TechnicalName,
    EventCore EventCore) : IPoleEvent;
