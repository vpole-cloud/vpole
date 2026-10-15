using Pole.Features.Secrets.Domain;

namespace Pole.Features.Secrets.Application.Persistence;

internal sealed record SecretProjectionMutationRecorded(SecretProjection Projection);

internal sealed record SecretProjectionMutationSkipped;

internal union SecretProjectionMutationOutcome(
    SecretProjectionMutationRecorded,
    SecretProjectionMutationSkipped);
