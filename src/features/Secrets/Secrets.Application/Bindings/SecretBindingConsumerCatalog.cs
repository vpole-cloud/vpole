// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using Pole.Features.GitOps.Contracts;
using Pole.Features.Secrets.Application.Declarations;
using Pole.Features.Secrets.Contracts;
using Pole.Features.Secrets.Domain;
using Pole.Features.Secrets.Primitives;
using Pole.Workloads;

namespace Pole.Features.Secrets.Application.Bindings;

public sealed class SecretBindingConsumerCatalog
{
    private readonly IReadOnlyDictionary<string, SecretDeclarationConsumer> consumers;
    private readonly IReadOnlyDictionary<string, SecretDeclarationConsumer> byResourceType;

    public SecretBindingConsumerCatalog(IEnumerable<SecretDeclarationConsumer> capabilities)
    {
        var definitions = Validate(capabilities);
        consumers = definitions.ToDictionary(item => item.Kind, StringComparer.Ordinal);
        byResourceType = definitions.ToDictionary(item => item.ResourceType.Value, StringComparer.Ordinal);
    }

    public IEnumerable<SecretDeclarationConsumer> All => consumers.Values;

    public SecretDeclarationConsumer? Find(ResourceTypeId type) => byResourceType.GetValueOrDefault(type.Value);

    public bool AllowsBeforePlacement(ResourceTypeId type) =>
        Find(type)?.Placement == SecretConsumerPlacementRequirement.AllowBeforePlacement;

    public bool AcceptsBindingPurpose(SecretBinding binding)
    {
        var capability = byResourceType.GetValueOrDefault(binding.Consumer.ResourceType);
        return capability?.Projection != SecretConsumerProjectionPolicy.DeclaredPurposeForAllDeployments ||
            binding.Usage == capability.Usage && binding.Selection is AllSecretDeployments;
    }

    public bool GrantsProjection(SecretBinding binding)
    {
        if (!binding.GrantsRuntimeProjection())
            return false;
        var capability = byResourceType.GetValueOrDefault(binding.Consumer.ResourceType);
        return capability?.Projection switch
        {
            null or SecretConsumerProjectionPolicy.RuntimeBindings => binding.Usage == SecretBindingUsage.Runtime,
            SecretConsumerProjectionPolicy.DeclaredPurposeForAllDeployments => binding.Usage == capability.Usage &&
                binding.Selection is AllSecretDeployments,
            _ => throw new ArgumentOutOfRangeException(nameof(capability)),
        };
    }

    public bool TryGet(string kind, [NotNullWhen(true)] out SecretDeclarationConsumer? consumer) =>
        consumers.TryGetValue(kind, out consumer);

    public SecretDeclarationConsumer Get(string kind) => consumers[kind];

    private static ImmutableArray<SecretDeclarationConsumer> Validate(
        IEnumerable<SecretDeclarationConsumer> capabilities)
    {
        var definitions = capabilities.ToImmutableArray();
        if (definitions.Any(item => string.IsNullOrWhiteSpace(item.Kind) ||
                !ResourceTypeId.TryCreate(item.ResourceType.Value, out _) || !Enum.IsDefined(item.Usage) ||
                !Enum.IsDefined(item.Projection) || !Enum.IsDefined(item.Placement) ||
                !Enum.IsDefined(item.ValueAccess)))
            throw new InvalidOperationException("Secret consumer capabilities require valid identities and policies.");
        return definitions;
    }

    public JsonElement NormalizeAndValidate(JsonElement spec)
    {
        var value = DeclarationJson.Read<SecretBindingDeclaration>(spec);
        DeclarationJson.Require(
            DeclarationNames.Valid(value.Secret) && value.Consumer is not null &&
            DeclarationNames.Valid(value.Consumer.Name) && TryGet(value.Consumer.Kind, out var consumer) &&
            consumer.Usage == value.Usage && value.KeyMappings is { Count: <= SecretKeyReferenceRules.MaximumKeys } &&
            value.KeyMappings.All(pair => SecretKeyReferenceRules.ValidKey(pair.Key) &&
                SecretKeyReferenceRules.ValidKey(pair.Value)) &&
            value.KeyMappings.Values.Distinct().Count() == value.KeyMappings.Count,
            DeclarationFieldPaths.Spec);
        return DeclarationJson.Write(value);
    }
}
