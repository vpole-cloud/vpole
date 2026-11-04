using Pole.Features.Secrets.Primitives;
using Pole.Workloads;

namespace Pole.Features.Secrets.Contracts;

/// <summary>The resource owner contributes its supported declaration binding capability.</summary>
public sealed record SecretDeclarationConsumer(
    string Kind,
    ResourceTypeId ResourceType,
    SecretBindingUsage Usage);
