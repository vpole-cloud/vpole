// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

using MongoDB.Driver;
using Pole.Database;
using Pole.Features.GitOps.Contracts;
using Pole.Features.Secrets.Contracts;
using Pole.Features.Secrets.Domain;
using Pole.Workloads;

namespace Pole.Features.Secrets.Application.Mongo;

internal static class ManagedSecretFilters
{
    public static FilterDefinition<Secret> Owner(string tenantId, ManagedSecretOwner owner) =>
        Builders<Secret>.Filter.Eq(x => x.TenantId, SecretIdentity.ObjectId(tenantId)) &
        Builders<Secret>.Filter.OfType<SecretManagement, ProviderManagedSecret>(x => x.Management) &
        Builders<Secret>.Filter.Eq(
            x => ((ProviderManagedSecret)x.Management).ResourceType,
            ResourceTypeId.Create(owner.ResourceType).Value) & Builders<Secret>.Filter.Eq(
            x => ((ProviderManagedSecret)x.Management).ResourceId,
            SecretIdentity.ObjectId(owner.ResourceId));

    public static FilterDefinition<Secret> Adoption(ManagedSecretAdoptionSnapshot request) =>
        Owner(request.Request.TenantId, request.Request.Owner) &
        SecretFilters.Identity(request.Request.TenantId, request.SecretId) &
        Builders<Secret>.Filter.Eq(item => item.ResourceGroupId, request.Request.ResourceGroupId) &
        Builders<Secret>.Filter.Eq(item => item.IsolationScopeId, request.Request.IsolationScopeId) &
        Builders<Secret>.Filter.Eq(item => item.Revision, request.Revision) &
        Builders<Secret>.Filter.Eq(item => item.ReferenceFence, request.ReferenceFence) &
        Builders<Secret>.Filter.Eq(item => item.CurrentVersion, request.ValueVersion);
}

internal static class SecretFilters
{
    public static FilterDefinition<Secret> AtPlanningVersion(Secret secret) => Identity(secret.TenantId, secret.Id) &
        Builders<Secret>.Filter.Eq(item => item.Revision, secret.Revision) &
        Builders<Secret>.Filter.Eq(item => item.ReferenceFence, secret.ReferenceFence);

    public static FilterDefinition<Secret> DeclarationBindingId(string tenant, string id) =>
        Builders<Secret>.Filter.Eq(item => item.TenantId, tenant) & Builders<Secret>.Filter.ElemMatch(
            item => item.Bindings,
            binding => binding.Id == id);

    public static FilterDefinition<Secret> DeclarationBindingName(string tenant, string name) =>
        Builders<Secret>.Filter.Eq(item => item.TenantId, tenant) & Builders<Secret>.Filter.ElemMatch(
            item => item.Bindings,
            binding => binding.DeclarationName == name);

    public static FilterDefinition<Secret> TechnicalName(string tenant, string name) =>
        Builders<Secret>.Filter.Eq(item => item.TenantId, tenant) &
        Builders<Secret>.Filter.Eq(item => item.NormalizedName, name);

    internal static FilterDefinition<Secret> Identity(string tenantId, string id) =>
        Builders<Secret>.Filter.ForTenant(SecretIdentity.ObjectId(tenantId)) &
        Builders<Secret>.Filter.Eq(x => x.Id, SecretIdentity.ObjectId(id));

    internal static FilterDefinition<Secret> Page(string tenantId, string? after) => after is null
        ? Builders<Secret>.Filter.ForTenant(SecretIdentity.ObjectId(tenantId))
        : Builders<Secret>.Filter.ForTenant(SecretIdentity.ObjectId(tenantId)) &
        Builders<Secret>.Filter.Gt(x => x.Id, SecretIdentity.ObjectId(after));

    internal static FilterDefinition<Secret> Scope(
        string tenantId,
        string groupId,
        string scopeId) => Builders<Secret>.Filter.ForTenant(SecretIdentity.ObjectId(tenantId)) &
        Builders<Secret>.Filter.Eq(x => x.ResourceGroupId, SecretIdentity.ObjectId(groupId)) &
        Builders<Secret>.Filter.Eq(x => x.IsolationScopeId, SecretIdentity.ObjectId(scopeId));

    internal static FilterDefinition<SecretVersion> Versions(string tenantId, string id) =>
        Builders<SecretVersion>.Filter.ForTenant(SecretIdentity.ObjectId(tenantId)) &
        Builders<SecretVersion>.Filter.Eq(x => x.SecretId, SecretIdentity.ObjectId(id));

    internal static FilterDefinition<SecretVersion> Version(
        string tenantId,
        string secretId,
        long version) => Versions(tenantId, secretId) & Builders<SecretVersion>.Filter.Eq(x => x.Version, version);

    internal static FilterDefinition<Secret> Consumer(
        string tenantId,
        string resourceType,
        string resourceId) => Builders<Secret>.Filter.ForTenant(SecretIdentity.ObjectId(tenantId)) &
        Builders<Secret>.Filter.ElemMatch(
            item => item.Bindings,
            binding => binding.Consumer.ResourceType == resourceType && binding.Consumer.ResourceId == resourceId);

    internal static FilterDefinition<Secret> Consumer(
        string tenantId,
        string groupId,
        string resourceType,
        string resourceId) => Builders<Secret>.Filter.ForTenant(SecretIdentity.ObjectId(tenantId)) &
        Builders<Secret>.Filter.Eq(x => x.ResourceGroupId, SecretIdentity.ObjectId(groupId)) &
        Builders<Secret>.Filter.ElemMatch(
            x => x.Bindings,
            x => x.Consumer.ResourceType == resourceType && x.Consumer.ResourceId == resourceId);
}

internal static class SecretLifecycleFilters
{
    public static FilterDefinition<Secret>? Selection(
        ReadLifecycleDependenciesRequest request,
        IReadOnlyDictionary<string, SecretDeclarationConsumer> kinds)
    {
        var tenant = Builders<Secret>.Filter.Eq(item => item.TenantId, request.TenantId);
        var id = request.Resource.ResourceId;
        if (request.Resource.Address.Kind == "Secret")
            return tenant & Builders<Secret>.Filter.Eq(item => item.Id, id);
        if (request.Resource.Address.Kind == "ResourceGroup")
            return tenant & Builders<Secret>.Filter.Eq(item => item.ResourceGroupId, id);
        if (request.Resource.Address.Kind == "IsolationScope")
            return tenant & Builders<Secret>.Filter.Eq(item => item.IsolationScopeId, id);
        return kinds.TryGetValue(request.Resource.Address.Kind, out var consumer)
            ? tenant & Builders<Secret>.Filter.ElemMatch(
                item => item.Bindings,
                binding => binding.Consumer.ResourceType == consumer.ResourceType.Value &&
                    binding.Consumer.ResourceId == id)
            : null;
    }
}

internal static class SecretProjectionFilters
{
    public static FilterDefinition<SecretProjection> Identity(string tenantId, string id) =>
        Builders<SecretProjection>.Filter.Eq(x => x.TenantId, tenantId) &
        Builders<SecretProjection>.Filter.Eq(x => x.Id, id);

    public static FilterDefinition<SecretProjection> ForSecret(string tenantId, string secretId) =>
        Builders<SecretProjection>.Filter.Eq(x => x.TenantId, SecretIdentity.ObjectId(tenantId)) &
        Builders<SecretProjection>.Filter.Eq(x => x.SecretId, SecretIdentity.ObjectId(secretId));

    public static FilterDefinition<SecretProjection> Revision(long revision) =>
        Builders<SecretProjection>.Filter.Eq(x => x.StorageRevision, revision);

    public static FilterDefinition<SecretProjection> Lease(SecretProjectionLease lease) =>
        Builders<SecretProjection>.Filter.Eq(x => x.Lease, lease);

    public static FilterDefinition<Secret> SourceCandidates(string? afterSecretId) => afterSecretId is null
        ? Builders<Secret>.Filter.Empty
        : Builders<Secret>.Filter.Gt(x => x.Id, afterSecretId);

    public static FilterDefinition<SecretProjection> RecoveryCandidates(
        string? afterSecretId,
        DateTime deletedAuditBefore)
    {
        var filter = Builders<SecretProjection>.Filter.Not(
                Builders<SecretProjection>.Filter
                    .OfType<SecretProjectionState, DeletedSecretProjection>(x => x.State)) |
            Builders<SecretProjection>.Filter.Lt(x => x.LastAttemptAtUtc, deletedAuditBefore);
        return afterSecretId is null
            ? filter
            : filter & Builders<SecretProjection>.Filter.Gt(x => x.SecretId, afterSecretId);
    }
}
