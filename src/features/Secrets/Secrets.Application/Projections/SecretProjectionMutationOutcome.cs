using Pole.Features.Secrets.Application.Values;
using Pole.Features.Secrets.Application.Secrets;
using Pole.Features.Secrets.Application.References;
using Pole.Features.Secrets.Application.Mongo;
using Pole.Features.Secrets.Application.Managed;
using Pole.Features.Secrets.Application.Lifecycle;
using Pole.Features.Secrets.Application.Declarations;
using Pole.Features.Secrets.Application.Bootstrap;
using Pole.Features.Secrets.Application.Bindings;
using Pole.Features.Secrets.Application;
using Pole.Features.Secrets.Domain;

namespace Pole.Features.Secrets.Application.Projections;

internal sealed record SecretProjectionMutationRecorded(SecretProjection Projection);

internal sealed record SecretProjectionMutationSkipped;

internal union SecretProjectionMutationOutcome(
    SecretProjectionMutationRecorded,
    SecretProjectionMutationSkipped);
