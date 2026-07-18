using System.Collections.Immutable;
using Pole.Features.Functions.Domain;

namespace Pole.Features.Functions.Application.Common;

public sealed record FunctionAppDto(
    string Id,
    string TenantId,
    string ResourceGroupId,
    string Name,
    string FunctionRoute,
    string? ActiveRevisionId,
    string? PendingRevisionId,
    int NextRevisionNumber,
    FunctionAppDesiredState DesiredState,
    FunctionAppStatus Status,
    FunctionQuotaAllocation AdmittedQuota,
    ImmutableHashSet<string> Tags,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc);

public sealed record FunctionRevisionDto(
    string Id,
    string FunctionAppId,
    int RevisionNumber,
    FunctionRevisionSpec Spec,
    FunctionRevisionStatus Status,
    DateTimeOffset CreatedAtUtc);

public sealed record FunctionAppDetailsDto(
    FunctionAppDto FunctionApp,
    FunctionRevisionDto? ActiveRevision,
    FunctionRevisionDto? PendingRevision);

public static class FunctionAppDtoExtensions
{
    public static FunctionAppDto ToDto(this FunctionApp app) =>
        new(
            app.Id,
            app.TenantId,
            app.ResourceGroupId,
            app.Name,
            app.FunctionRoute,
            app.ActiveRevisionId,
            app.PendingRevisionId,
            app.NextRevisionNumber,
            app.DesiredState,
            app.Status,
            app.AdmittedQuota,
            app.Tags,
            app.CreatedAtUtc,
            app.UpdatedAtUtc);

    public static FunctionRevisionDto ToDto(this FunctionRevision revision) =>
        new(
            revision.Id,
            revision.FunctionAppId,
            revision.RevisionNumber,
            revision.Spec,
            revision.Status,
            revision.CreatedAtUtc);
}
