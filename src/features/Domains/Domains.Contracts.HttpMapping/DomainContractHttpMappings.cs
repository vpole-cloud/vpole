// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

using System.Net;
using Pole.ASP.Helpers;

namespace Pole.Features.Domains.Contracts.HttpMapping;

public static class DomainContractHttpMappings
{
    public static IReadOnlyList<HttpProblemMapping> All { get; } = Array.AsReadOnly<HttpProblemMapping>(
    [
        HttpProblemMapping.For<GeneratedDomainZoneError>(MapGeneratedZoneError),
        HttpProblemMapping.For<GeneratedHostnameBindingError>(MapGeneratedBindingError),
        HttpProblemMapping.For<CustomHostnameBindingGitOwned>(MapCustomHostnameBindingGitOwned),
    ]);

    public static HttpProblem MapCustomHostnameBindingGitOwned(CustomHostnameBindingGitOwned owned) => Problem(
        HttpStatusCode.Conflict,
        "domains.hostname.git-owned",
        "Hostname binding configuration is managed by GitOps",
        ("bindingId", owned.BindingId));

    public static HttpProblem MapGeneratedZoneError(GeneratedDomainZoneError error) => error switch
    {
        GeneratedDomainZoneTenantUnavailable unavailable => Problem(
            HttpStatusCode.NotFound,
            "domains.generated-zone.tenant-unavailable",
            "Tenant identity is unavailable",
            ("tenantId", unavailable.TenantId)),
        GeneratedDomainZoneTargetChanged changed => Problem(
            HttpStatusCode.Conflict,
            "domains.generated-zone.target-changed",
            "Deployment target changed while reserving a generated domain zone",
            ("deploymentTargetIds", changed.DeploymentTargetIds)),
        GeneratedDomainZoneMismatch mismatch => Problem(
            HttpStatusCode.Conflict,
            "domains.generated-zone.domain-mismatch",
            "Generated domain zone does not match the target public domain",
            ("deploymentTargetId", mismatch.DeploymentTargetId)),
        GeneratedDomainZoneConflict conflict => Problem(
            HttpStatusCode.Conflict,
            "domains.generated-zone.concurrent-change",
            "Generated domain zone changed concurrently",
            ("deploymentTargetIds", conflict.DeploymentTargetIds)),
    };

    public static HttpProblem MapGeneratedBindingError(GeneratedHostnameBindingError error) => error switch
    {
        GeneratedHostnameBindingZoneNotFound notFound => Problem(
            HttpStatusCode.NotFound,
            "domains.generated-binding.zone-not-found",
            "Domain zone not found",
            ("tenantDomainZoneId", notFound.TenantDomainZoneId)),
        GeneratedHostnameBindingTargetMismatch mismatch => Problem(
            HttpStatusCode.Conflict,
            "domains.generated-binding.target-mismatch",
            "Domain zone belongs to another deployment target",
            ("tenantDomainZoneId", mismatch.TenantDomainZoneId),
            ("deploymentTargetId", mismatch.DeploymentTargetId)),
        GeneratedHostnameBindingZoneChanged changed => Problem(
            HttpStatusCode.Conflict,
            "domains.generated-binding.zone-changed",
            "Domain zone changed concurrently",
            ("tenantDomainZoneIds", changed.TenantDomainZoneIds)),
        GeneratedHostnameBindingConflict conflict => Problem(
            HttpStatusCode.Conflict,
            "domains.generated-binding.conflict",
            "Generated hostname is already reserved",
            ("hostnames", conflict.Hostnames)),
    };

    public static HttpProblem MapCustomHostnameBindingError(CustomHostnameBindingError error) => error switch
    {
        CustomHostnameBindingInvalidRelativeName => Problem(
            HttpStatusCode.BadRequest,
            "domains.hostname.invalid-name",
            "The relative name does not form a valid hostname in this zone"),
        CustomHostnameBindingZoneNotFound notFound => Problem(
            HttpStatusCode.NotFound,
            "domains.hostname.zone-not-found",
            "Domain zone not found",
            ("domainZoneId", notFound.TenantDomainZoneId)),
        CustomHostnameBindingTargetMismatch mismatch => Problem(
            HttpStatusCode.Conflict,
            "domains.hostname.zone-target-mismatch",
            "Domain zone belongs to another deployment target",
            ("domainZoneId", mismatch.TenantDomainZoneId),
            ("deploymentTargetId", mismatch.DeploymentTargetId)),
        CustomHostnameBindingNameConflict conflict => Problem(
            HttpStatusCode.Conflict,
            "domains.hostname.name-conflict",
            "Hostname binding technical name is already used",
            ("technicalName", conflict.TechnicalName)),
        CustomHostnameBindingConflict conflict => Problem(
            HttpStatusCode.Conflict,
            "domains.hostname.already-bound",
            "Hostname is already bound",
            ("hostname", conflict.Hostname)),
        CustomHostnameBindingNotFound notFound => Problem(
            HttpStatusCode.NotFound,
            "domains.hostname.not-found",
            "Hostname binding not found",
            ("bindingId", notFound.BindingId)),
        GeneratedHostnameBindingDeletionRejected rejected => Problem(
            HttpStatusCode.Conflict,
            "domains.hostname.delete.generated",
            "Generated hostname binding cannot be deleted",
            ("bindingId", rejected.BindingId)),
        CustomHostnameBindingZoneUnavailable unavailable => Problem(
            HttpStatusCode.Conflict,
            "domains.hostname.zone-unavailable",
            "Hostname binding domain zone is unavailable",
            ("domainZoneId", unavailable.TenantDomainZoneId)),
        CustomHostnameBindingZoneChanged changed => Problem(
            HttpStatusCode.Conflict,
            "domains.hostname.zone-changed",
            "Domain zone changed during hostname update",
            ("domainZoneIds", changed.TenantDomainZoneIds)),
        CustomHostnameBindingDeletionConflict conflict => Problem(
            HttpStatusCode.Conflict,
            "domains.hostname.delete.conflict",
            "Hostname binding changed during deletion",
            ("bindingId", conflict.BindingId)),
    };

    private static HttpProblem Problem(
        HttpStatusCode status,
        string code,
        string title,
        params (string Name, object Value)[] extensions) => new(
        (int)status,
        PoleProblemTypeId.FromCode(code),
        title,
        code,
        extensions: extensions.ToDictionary(extension => extension.Name, extension => extension.Value));
}
