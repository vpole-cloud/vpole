// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Pole.Features.Events.Abstractions;
using Pole.Features.Realtime.Contracts;
using Pole.Features.ResourceDeployments.Application;
using Pole.Features.ResourceDeployments.Application.Realtime;
using Pole.Features.ResourceDeployments.Events;
using Pole.Features.SourceBuilds.Application;
using Pole.Features.WebServices.Application;
using Pole.Features.WebServices.Application.Realtime;
using Pole.Features.WebServices.Domain;
using Pole.Features.WebServices.Events;
using Xunit;

namespace Pole.Tests.Integration.ResourceDeployments;

public sealed class PilotRealtimeConsumerTests
{
    private const string TenantId = "tenant-pilot";
    private const string ResourceId = "resource-pilot";
    private const string GroupId = "group-pilot";

    [Fact]
    public void Owners_RegisterEachMetadataAndObservationTopicOnceThroughContracts()
    {
        var services = new ServiceCollection();
        var configuration = new ConfigurationBuilder().Build();
        services.AddSourceBuildsFeature(configuration)
            .AddResourceDeploymentsFeature(configuration)
            .AddWebServicesFeature();
        var topics = services.Where(descriptor => descriptor.ServiceType == typeof(RealtimeTopic))
            .Select(descriptor => Assert.IsType<RealtimeTopic>(descriptor.ImplementationInstance))
            .ToArray();
        Assert.Equal(
            ["operations", "runtime-observations", "source-builds", "web-services"],
            topics.Select(topic => topic.Name).Order());
        Assert.All(topics, topic => Assert.Equal(RealtimeScope.Tenant, topic.Scope));
    }

    [Fact]
    public async Task HistoricalRequests_DeserializeAndProduceSafeTenantHints()
    {
        var json = JsonSerializer.Serialize(
            new
            {
                ResourceDeploymentId = "physical-pilot",
                RuntimeCellId = "cell-pilot",
                Generation = 3,
                ResourceRevision = 2,
                EventCore = EventCore.Create(TenantId),
            },
            EventExtensions.SerializerOptions);
        var apply = JsonSerializer.Deserialize<ResourceDeploymentReconciliationRequestedEvent>(
            json,
            EventExtensions.SerializerOptions)!;
        var delete = JsonSerializer.Deserialize<ResourceDeploymentDeletionRequestedEvent>(
            json,
            EventExtensions.SerializerOptions)!;
        Assert.Null(apply.ResourceType);
        Assert.Null(apply.ResourceId);
        Assert.Null(apply.ResourceGroupId);
        Assert.Null(delete.ResourceType);
        Assert.Null(delete.ResourceId);
        Assert.Null(delete.ResourceGroupId);
        var notices = new List<RealtimeChange>();

        Task Publish(RealtimeChange change, CancellationToken _)
        {
            notices.Add(change);
            return Task.CompletedTask;
        }

        var operations = new NotifyResourceDeploymentChangedConsumer(Publish);
        var webServices = new NotifyWebServiceChangedConsumer(
            Publish,
            (
                _, _,
                _) => throw new InvalidOperationException("Historical hints must not use foreign collection reads."));
        await operations.HandleAsync(apply);
        await operations.HandleAsync(delete);
        await webServices.HandleAsync(apply);
        await webServices.HandleAsync(delete);
        Assert.Equal(4, notices.Count);
        Assert.All(
            notices,
            notice =>
            {
                Assert.Equal(TenantId, notice.TenantId);
                Assert.Null(notice.ResourceId);
                Assert.Null(notice.ResourceGroupId);
            });
    }

    [Fact]
    public async Task DeploymentLifecycle_EmitsOperationsAndWebServiceHints_WithoutFailureDetails()
    {
        var notices = new List<RealtimeChange>();

        Task Publish(RealtimeChange change, CancellationToken _)
        {
            notices.Add(change);
            return Task.CompletedTask;
        }

        var operations = new NotifyResourceDeploymentChangedConsumer(Publish);
        var webServices = new NotifyWebServiceChangedConsumer(
            Publish,
            (
                _, _,
                _) => throw new InvalidOperationException("Immutable identity is sufficient."));
        var core = EventCore.Create(TenantId);
        var requested = new ResourceDeploymentReconciliationRequestedEvent("physical-pilot", "cell-pilot", 3, 2)
        {
            ResourceType = "pole/web-service",
            ResourceId = ResourceId,
            ResourceGroupId = GroupId,
            EventCore = core,
        };
        var deletion = new ResourceDeploymentDeletionRequestedEvent("physical-pilot", "cell-pilot", 3, 2)
        {
            ResourceType = "pole/web-service",
            ResourceId = ResourceId,
            ResourceGroupId = GroupId,
            EventCore = core,
        };
        var started = new ResourceDeploymentReconciliationStartedEvent(
            "physical-pilot",
            "pole/web-service",
            ResourceId,
            GroupId,
            "cell-pilot",
            3,
            "Apply",
            2)
        {
            EventCore = core,
        };
        var applied = new ResourceDeploymentAppliedEvent(
            "physical-pilot",
            "pole/web-service",
            ResourceId,
            "cell-pilot",
            3,
            2)
        {
            ResourceGroupId = GroupId,
            EventCore = core,
        };
        var failed = new ResourceDeploymentFailedEvent(
            "physical-pilot",
            "pole/web-service",
            ResourceId,
            "cell-pilot",
            3,
            "Apply",
            "safe-code",
            true,
            2)
        {
            ResourceGroupId = GroupId,
            EventCore = core,
        };
        await operations.HandleAsync(requested);
        await webServices.HandleAsync(requested);
        await operations.HandleAsync(deletion);
        await webServices.HandleAsync(deletion);
        await operations.HandleAsync(started);
        await webServices.HandleAsync(started);
        await operations.HandleAsync(applied);
        await webServices.HandleAsync(applied);
        await operations.HandleAsync(failed);
        await webServices.HandleAsync(failed);
        Assert.Equal(10, notices.Count);
        Assert.All(
            notices,
            notice =>
            {
                Assert.Equal(ResourceId, notice.ResourceId);
                Assert.Equal(GroupId, notice.ResourceGroupId);
                Assert.Equal(TenantId, notice.TenantId);
            });
        Assert.DoesNotContain("safe-code", JsonSerializer.Serialize(notices), StringComparison.Ordinal);
        await webServices.HandleAsync(
            failed with
            {
                ResourceType = "pole/redis-service",
            });
        Assert.Equal(10, notices.Count);
    }

    [Fact]
    public void HistoricalCompletionPayloads_DeserializeWithoutGroupSnapshot()
    {
        var json = JsonSerializer.Serialize(
            new
            {
                ResourceDeploymentId = "physical-pilot",
                ResourceType = "pole/web-service",
                ResourceId,
                RuntimeCellId = "cell-pilot",
                Generation = 3,
                ResourceRevision = 2,
                Operation = "Apply",
                FailureCode = "safe-code",
                Retryable = true,
                EventCore = EventCore.Create(TenantId),
            },
            EventExtensions.SerializerOptions);
        Assert.Null(
            JsonSerializer.Deserialize<ResourceDeploymentAppliedEvent>(json, EventExtensions.SerializerOptions)!
                .ResourceGroupId);
        Assert.Null(
            JsonSerializer.Deserialize<ResourceDeploymentFailedEvent>(json, EventExtensions.SerializerOptions)!
                .ResourceGroupId);
        Assert.Null(
            JsonSerializer.Deserialize<ResourceDeploymentDeletedEvent>(json, EventExtensions.SerializerOptions)!
                .ResourceGroupId);
    }

    [Fact]
    public async Task WebServiceUpdateAndReleaseCompletion_NotifyOwnTopicAndUseTenantScopedGroupLookup()
    {
        var notices = new List<RealtimeChange>();
        var lookups = new List<(string Tenant, string Resource)>();
        var consumer = new NotifyWebServiceChangedConsumer(
            (change, _) =>
            {
                notices.Add(change);
                return Task.CompletedTask;
            },
            (
                tenant, resource,
                _) =>
            {
                lookups.Add((tenant, resource));
                return Task.FromResult<WebService?>(null);
            });
        var core = EventCore.Create(TenantId);
        await consumer.HandleAsync(
            new WebServiceUpdatedEvent(
                ResourceId,
                "name-pilot",
                GroupId,
                "scope-pilot",
                2,
                WebServiceLifecycleState.Active,
                null!,
                null!,
                [],
                [])
            {
                EventCore = core,
            });
        await consumer.HandleAsync(
            new WebServiceDeploymentCompletedEvent(
                "release-pilot",
                ResourceId,
                "physical-pilot",
                2,
                3,
                "Apply",
                false,
                false,
                "failure-pilot",
                "sensitive failure detail",
                "Failed",
                true)
            {
                EventCore = core,
            });
        Assert.Equal([(TenantId, ResourceId)], lookups);
        Assert.Equal(2, notices.Count);
        Assert.All(
            notices,
            notice =>
            {
                Assert.Equal("web-services", notice.Topic);
                Assert.Equal(TenantId, notice.TenantId);
                Assert.Equal(ResourceId, notice.ResourceId);
            });
        Assert.Equal(GroupId, notices[0].ResourceGroupId);
        Assert.DoesNotContain("sensitive failure detail", JsonSerializer.Serialize(notices), StringComparison.Ordinal);
    }
}
