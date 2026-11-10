using System.Collections.Immutable;
using Pole.Features.Quotas.Domain;

namespace Pole.Features.Quotas.Application.Common;

public sealed record QuotaValueDto(
    string Key,
    long Value
);

/// <summary>
///     A null limit means that the tenant has no cap for this quota.
/// </summary>
public sealed record QuotaLimitDto(
    string Key,
    long? Limit
);

public sealed record TenantQuotaPolicyDto(
    string TenantId,
    IReadOnlyCollection<QuotaLimitDto> Limits,
    DateTime? CreatedAtUtc,
    DateTime? UpdatedAtUtc
);

public sealed record TenantQuotaUsageDto(
    string TenantId,
    IReadOnlyCollection<QuotaValueDto> CommittedUsage,
    IReadOnlyCollection<QuotaValueDto> ActiveReservations,
    IReadOnlyCollection<QuotaLimitDto> Limits,
    DateTime? PolicyUpdatedAtUtc,
    DateTime? UsageUpdatedAtUtc
);

public sealed record UpsertTenantQuotaPolicyRequestBody(
    IReadOnlyCollection<QuotaValueDto>? Limits
);

public static class QuotaDtoExtensions
{
    public static TenantQuotaPolicyDto ToDto(
        this TenantQuotaPolicy? policy,
        string tenantId,
        QuotaCatalog catalog) => new(
        tenantId,
        ToLimitDtos(policy?.Limits, catalog),
        policy?.CreatedAtUtc,
        policy?.UpdatedAtUtc);

    public static TenantQuotaUsageDto ToDto(
        this TenantQuotaUsage? usage,
        IReadOnlyCollection<QuotaReservation> activeReservations,
        TenantQuotaPolicy? policy,
        string tenantId,
        QuotaCatalog catalog)
    {
        var activeValues = QuotaValues.Sum(activeReservations.Select(reservation => reservation.Delta));
        return new TenantQuotaUsageDto(
            tenantId,
            ToValueDtos(usage?.Values, catalog),
            ToValueDtos(activeValues, catalog),
            ToLimitDtos(policy?.Limits, catalog),
            policy?.UpdatedAtUtc,
            usage?.UpdatedAtUtc);
    }

    public static QuotaDelta ToQuotaLimits(this UpsertTenantQuotaPolicyRequestBody body, QuotaCatalog catalog)
    {
        var errors = ValidateQuotaLimits(body, catalog);
        if (errors.Count > 0)
            throw new ArgumentException("Tenant quota policy is invalid.", nameof(body));

        var definitions = catalog.Definitions.ToDictionary(
            definition => definition.Key.Value,
            definition => definition.Key,
            StringComparer.OrdinalIgnoreCase);
        return QuotaDelta.Create(body.Limits!.Select(limit => new QuotaValue(definitions[limit.Key], limit.Value)));
    }

    public static IReadOnlyDictionary<string, string[]> ValidateQuotaLimits(
        UpsertTenantQuotaPolicyRequestBody? body,
        QuotaCatalog catalog)
    {
        var errors = new Dictionary<string, string[]>();
        if (body?.Limits is null)
        {
            errors["Body.Limits"] = ["Limits is required. Send an empty collection to make every quota unlimited."];
            return errors;
        }

        var definitions = catalog.Definitions.ToDictionary(
            definition => definition.Key.Value,
            definition => definition.Key,
            StringComparer.OrdinalIgnoreCase);
        var seen = new HashSet<QuotaKey>();
        foreach (var (limit, index) in body.Limits.Select((limit, index) => (limit, index)))
        {
            if (limit is null || string.IsNullOrWhiteSpace(limit.Key) ||
                !definitions.TryGetValue(limit.Key, out var key))
            {
                errors[$"Body.Limits[{index}].Key"] = ["Quota key is invalid."];
                continue;
            }

            if (!seen.Add(key))
                errors[$"Body.Limits[{index}].Key"] = ["Quota key must be specified once."];

            if (limit.Value <= 0)
                errors[$"Body.Limits[{index}].Value"] = ["Quota limit must be greater than zero."];
        }

        return errors;
    }

    private static IReadOnlyCollection<QuotaValueDto> ToValueDtos(IEnumerable<QuotaValue>? values, QuotaCatalog catalog)
    {
        var source = values ?? ImmutableList<QuotaValue>.Empty;
        return catalog.Definitions
            .Select(definition => new QuotaValueDto(definition.Key.Value, QuotaValues.GetValue(source, definition.Key)))
            .ToArray();
    }

    private static IReadOnlyCollection<QuotaLimitDto> ToLimitDtos(IEnumerable<QuotaValue>? limits, QuotaCatalog catalog)
    {
        var source = limits ?? ImmutableList<QuotaValue>.Empty;
        return catalog.Definitions
            .Select(definition =>
            {
                var configured = source.FirstOrDefault(limit => limit.Key == definition.Key);
                return new QuotaLimitDto(definition.Key.Value, configured?.Value);
            })
            .ToArray();
    }
}
