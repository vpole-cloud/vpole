// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

using Pole.Features.ResourceGroups.Application.Endpoints;
using Pole.Features.ResourceGroups.Domain;
using Pole.Features.WebServices.Application.Endpoints;
using Pole.Features.WebServices.Domain;

namespace Pole.Tests.Integration.Infrastructure;

public sealed class IntegrationTestData(
    IntegrationTestScope scope
)
{
    public string TenantId => scope.TenantId;
    public string UserId => scope.UserId;
    public string UserEmail => scope.UserEmail;
    public string Hostname(string label = "app") => scope.Hostname(label);

    public CreateResourceGroupRequest CreateResourceGroupRequest(
        string name = "alpha",
        string description = "primary group") => TestData.CreateResourceGroupRequest(TenantId, name, description);

    public CreateWebServiceRequest CreateWebServiceRequest(
        string resourceGroupId,
        string name = "web-api",
        string? tenantId = null) => TestData.CreateWebServiceRequest(
        resourceGroupId,
        tenantId ?? TenantId,
        UserId,
        UserEmail,
        name);

    public ResourceGroup SeedResourceGroup(string name = "alpha", string? tenantId = null) =>
        TestData.SeedResourceGroup(tenantId ?? TenantId, name);

    public WebService SeedWebService(
        string resourceGroupId,
        string? tenantId = null,
        string name = "web-api",
        string? id = null,
        long activeRevision = 1) =>
        TestData.SeedWebService(resourceGroupId, tenantId ?? TenantId, name, id, activeRevision);
}
