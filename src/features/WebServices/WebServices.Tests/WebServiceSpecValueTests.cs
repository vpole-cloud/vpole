// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

using System.Collections.Immutable;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using Pole.Features.WebServices.Application.Common;
using Pole.Features.WebServices.Domain;
using Xunit;

namespace Pole.Features.WebServices.Tests;

public sealed class WebServiceSpecValueTests
{
    [Fact]
    public void Equivalent_serialized_configuration_keeps_the_same_revision()
    {
        var service = ConfiguredService();
        var copied = Copy(service.Spec);

        Assert.NotSame(service.Spec.Config.PortMappings, copied.Config.PortMappings);
        Assert.NotSame(service.Spec.Config.EnvironmentVariables, copied.Config.EnvironmentVariables);
        Assert.Same(service, service.UpdateDesiredState(copied, service.Tags));
    }

    [Theory]
    [InlineData("environment")]
    [InlineData("environment-case")]
    [InlineData("ports")]
    [InlineData("connection")]
    [InlineData("secret")]
    [InlineData("image")]
    [InlineData("pull-reference")]
    [InlineData("autoscaling")]
    [InlineData("probes")]
    public void Configuration_changes_advance_the_revision(string field)
    {
        var service = ConfiguredService();
        var spec = Copy(service.Spec);
        var changed = field switch
        {
            "environment" => spec with
            {
                Config = spec.Config with
                {
                    EnvironmentVariables = spec.Config.EnvironmentVariables.SetItem("MODE", "next"),
                },
            },
            "environment-case" => spec with
            {
                Config = spec.Config with
                {
                    EnvironmentVariables = spec.Config
                        .EnvironmentVariables
                        .Remove("MODE")
                        .Add("mode", "production")
                        .WithComparers(StringComparer.OrdinalIgnoreCase),
                },
            },
            "ports" => spec with
            {
                Config = spec.Config with
                {
                    PortMappings = spec.Config.PortMappings.SetItem(
                        0,
                        spec.Config.PortMappings[0] with { TargetPort = "9090" }),
                },
            },
            "connection" => spec with
            {
                Config = spec.Config with
                {
                    ConnectionEnvironmentReferences = spec.Config.ConnectionEnvironmentReferences.SetItem(
                        "DB_HOST",
                        spec.Config.ConnectionEnvironmentReferences["DB_HOST"] with { ValueKind = "Port" }),
                },
            },
            "secret" => spec with
            {
                Config = spec.Config with
                {
                    SecretEnvironmentReferences = spec.Config.SecretEnvironmentReferences.SetItem(
                        "DB_PASSWORD",
                        spec.Config.SecretEnvironmentReferences["DB_PASSWORD"] with { Key = "rotated" }),
                },
            },
            "image" => spec with
            {
                Image = Assert.IsType<ImageInfo>(spec.Image) with { Tag = "next" },
            },
            "pull-reference" => spec with
            {
                Image = Assert.IsType<ImageInfo>(spec.Image) with
                {
                    SecretName = "another-pull-reference",
                },
            },
            "autoscaling" => spec with { Autoscaling = spec.Autoscaling! with { MaxReplicas = 5 } },
            "probes" => spec with
            {
                Probes = spec.Probes with { Readiness = spec.Probes.Readiness with { Path = "/next" } },
            },
            _ => throw new ArgumentOutOfRangeException(nameof(field)),
        };

        var updated = service.UpdateDesiredState(changed, service.Tags);

        Assert.Equal(service.ActiveRevision + 1, updated.ActiveRevision);
        Assert.Equal(service.Revision + 1, updated.Revision);
    }

    private static WebServiceSpec Copy(WebServiceSpec spec) =>
        BsonSerializer.Deserialize<WebServiceSpec>(spec.ToBson());

    private static WebService ConfiguredService()
    {
        var service = WebServiceFactory.Create(
            "web-api",
            WebServiceTestData.TenantId,
            "507f1f77bcf86cd799439003",
            ObjectId.GenerateNewId().ToString(),
            WebServiceTestData.CreateSpec().ToDomain(),
            WebServiceExposure.Create(),
            WebServiceFactory.DefaultOptions);
        return service with
        {
            Spec = service.Spec with
            {
                Image = Assert.IsType<ImageInfo>(service.Spec.Image) with
                {
                    SecretName = "managed-pull-reference",
                },
                Autoscaling = new AutoscalingSpec(1, 3, new AutoscalingMetric("cpu", 70)),
                Config = service.Spec.Config with
                {
                    EnvironmentVariables = ImmutableDictionary<string, string>.Empty
                        .Add("MODE", "production")
                        .Add("PORT", "8080"),
                    ConnectionEnvironmentReferences
                    = ImmutableDictionary<string, ConnectionEnvironmentReference>.Empty.Add(
                        "DB_HOST",
                        new ConnectionEnvironmentReference("507f1f77bcf86cd799439001", "Host", ConfigurationKey: null)),
                    SecretEnvironmentReferences = ImmutableDictionary<string, SecretEnvironmentReference>.Empty.Add(
                        "DB_PASSWORD",
                        new SecretEnvironmentReference("507f1f77bcf86cd799439002", "password")),
                },
            },
        };
    }
}
