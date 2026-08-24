using Pole.Features.StorageProfiles.Application.Discovery;

namespace Pole.Features.StorageProfiles.Application.Common;

public sealed record StorageProfileTargetMappingsInvalid(
    IReadOnlyDictionary<string, string[]> Errors);

public union StorageProfileMutationValidationError(
    StorageProfileTargetMappingsInvalid,
    KubernetesStorageDiscoveryUnavailable);

public sealed record StorageProfileRevisionConflict(string StorageProfileId);
