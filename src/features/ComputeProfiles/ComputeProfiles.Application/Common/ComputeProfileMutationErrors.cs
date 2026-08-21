namespace Pole.Features.ComputeProfiles.Application.Common;

public sealed record SelectedComputeProfileTenantsNotFound(
    IReadOnlyList<string> TenantIds);

public sealed record ComputeProfileTargetMappingsInvalid(
    IReadOnlyDictionary<string, string[]> Errors);

public sealed record DefaultComputeProfileCannotBeDemoted(
    string ComputeProfileId);

public sealed record ComputeProfileRevisionConflict(
    string ComputeProfileId);

public union ComputeProfileMutationValidationError(
    SelectedComputeProfileTenantsNotFound,
    ComputeProfileTargetMappingsInvalid);
