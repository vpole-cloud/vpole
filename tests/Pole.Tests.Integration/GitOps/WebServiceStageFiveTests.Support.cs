// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

using System.Collections.Immutable;
using System.Text.Json.Nodes;
using MongoDB.Driver;
using Pole.Features.DeploymentTargets.Domain;
using Pole.Features.GitOps.Compiler;
using Pole.Tests.Integration.DeploymentTargets;
using Pole.Tests.Integration.Infrastructure;

namespace Pole.Tests.Integration.GitOps;

public abstract partial class DeclarativePipelineTestBase
{
    private protected async Task EnablePublicWebTargetAsync()
    {
        var targets = Fixture.Database.GetCollection<DeploymentTarget>("DeploymentTargets");
        var target = await targets.Find(item => item.Id == TestData.DefaultDeploymentTargetId).SingleAsync();
        target = (target with
        {
            WorkloadConfiguration = DeploymentTargetWorkloadConfiguration.Create(
                DeploymentTargetIngressConfiguration.Create("traefik", "letsencrypt-prod", Data.Hostname("apps")),
                new DeploymentTargetNetworkSources(
                    ImmutableDictionary<string, DeploymentTargetNetworkSource>.Empty.Add(
                        "public-ingress",
                        DeploymentTargetCidrNetworkSource.Create("192.0.2.0/24")))),
            Revision = target.Revision + 1,
        }).AttestForTests();
        await targets.ReplaceOneAsync(item => item.Id == target.Id, target);
    }

    private protected static ManifestFile[] StageFiveWebSnapshot(
        Action<JsonNode> configure,
        bool withSecrets = false,
        string? scope = null)
    {
        var documents = Snapshot()[0].Content.Split("\n---\n").Select(item => JsonNode.Parse(item)!).ToList();
        var service = documents.Single(item => item["kind"]!.GetValue<string>() == "WebService")["spec"]!;
        if (!withSecrets)
        {
            documents.RemoveAll(item => item["kind"]!.GetValue<string>() is "Secret" or "SecretBinding");
            service["secretEnvironment"] = new JsonObject();
        }

        if (scope is not null)
        {
            service["placement"]!["isolationScope"] = scope;
            foreach (var secret in documents.Where(item => item["kind"]!.GetValue<string>() == "Secret"))
                secret["spec"]!["isolationScope"] = scope;
            documents.Add(
                new JsonObject
                {
                    ["apiVersion"] = "pole.io/v1alpha1",
                    ["kind"] = "IsolationScope",
                    ["metadata"] = new JsonObject { ["name"] = scope },
                    ["spec"] = new JsonObject { ["resourceGroup"] = "demo" },
                });
        }

        configure(service);
        return
        [
            new ManifestFile("stage-five.yaml", string.Join("\n---\n", documents.Select(item => item.ToJsonString()))),
        ];
    }
}
