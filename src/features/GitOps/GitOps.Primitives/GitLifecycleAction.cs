namespace Pole.Features.GitOps.Primitives;

public enum GitLifecycleAction { Adopt, Remove, Replace, Detach, Apply, Retain }

public enum GitLifecycleProtection { ConfigurationOnly, ProtectedData }

public enum GitLifecyclePresence { Active, Deleting }

public enum GitLifecycleVersionSemantics { ObservedDocuments, ManagedConfiguration }

public enum GitPreviewReferenceState { Existing, Planned }

public enum GitLifecycleMutationEffect { Adopt, Remove }

public enum GitLifecycleDependencyOrder
{
    BeforeRemoval,
    AfterRemovalRequested,
    AfterDependentRemovalRequested,
}
