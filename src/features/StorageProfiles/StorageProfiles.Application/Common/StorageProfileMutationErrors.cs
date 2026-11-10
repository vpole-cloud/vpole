namespace Pole.Features.StorageProfiles.Application.Common;

public sealed record StorageProfileTargetMappingsInvalid(
    IReadOnlyDictionary<string, string[]> Errors
);

public sealed record StorageProfileRevisionConflict(
    string StorageProfileId
);

public sealed record StorageProfileTechnicalNameConflict(
    string TechnicalName
);
