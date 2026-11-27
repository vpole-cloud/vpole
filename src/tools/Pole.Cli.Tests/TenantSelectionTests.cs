// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

using Pole.Cli.Api;
using Xunit;

namespace Pole.Cli.Tests;

public sealed class TenantSelectionTests
{
    private static readonly TenantDto[] Tenants =
    [
        new("tenant-1", "Production", "Owner", true),
        new("tenant-2", "Shared", "Member", false),
        new("tenant-3", "Shared", "Owner", false),
    ];

    [Fact]
    public void Selection_accepts_an_exact_tenant_id()
    {
        var result = TenantSelection.Resolve("tenant-1", Tenants);

        var selected = Assert.IsType<TenantSelectionFound>(result);
        Assert.Equal("tenant-1", selected.Tenant.Id);
    }

    [Fact]
    public void Selection_accepts_an_exact_unique_tenant_name()
    {
        var result = TenantSelection.Resolve("Production", Tenants);

        var selected = Assert.IsType<TenantSelectionFound>(result);
        Assert.Equal("tenant-1", selected.Tenant.Id);
    }

    [Fact]
    public void Selection_reports_duplicate_names_and_requires_an_id()
    {
        var result = TenantSelection.Resolve("Shared", Tenants);

        var ambiguous = Assert.IsType<TenantSelectionAmbiguous>(result);
        Assert.Equal(["tenant-2", "tenant-3"], ambiguous.Matches.Select(tenant => tenant.Id));
    }

    [Fact]
    public void Exact_id_takes_precedence_over_another_tenants_name()
    {
        var tenants = new[]
        {
            new TenantDto("target", "Other", "Owner", false),
            new TenantDto("other", "target", "Member", false),
        };

        var selected = Assert.IsType<TenantSelectionFound>(TenantSelection.Resolve("target", tenants));

        Assert.Equal("target", selected.Tenant.Id);
    }
}
