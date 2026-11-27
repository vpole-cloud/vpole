// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

namespace Pole.Features.Domains.Application.Mongo;

internal static class DomainIndexNames
{
    public const string ZoneTargetDomain = "UX_TenantDomainZones_Target_Domain";
    public const string ZoneTechnicalName = "UX_TenantDomainZones_Tenant_TechnicalName";
    public const string BindingHostname = "UX_HostnameBindings_Hostname";
    public const string BindingTechnicalName = "UX_HostnameBindings_Tenant_TechnicalName";
}
