# Pole documentation

Pole uses a hybrid documentation layout. Platform-wide rules and operational guides live in this
directory. Documentation that explains one feature's behavior lives beside that feature's code.

## Where documentation belongs

Use the following ownership rule for new documentation:

- `src/features/<Feature>/README.md` is the canonical entry point for a feature. It should explain
  the feature's purpose, supported scope, module boundaries, API surface, important flows,
  dependencies, and current limitations.
- `src/features/<Feature>/docs/*.md` contains deeper feature-specific design or operational notes
  when the README would otherwise become difficult to navigate.
- `docs/*.md` contains cross-cutting architecture, engineering policy, installation, testing, and
  runbooks that apply to multiple features.
- SDKs, examples, and other independently consumable packages keep usage documentation next to
  their package and link back to the owning feature.

Keep one canonical explanation for each behavior and link to it from other locations. A central
document may summarize a feature, but the feature-local README owns its implementation details.

When introducing a new feature README, prefer these sections when they are relevant:

1. Purpose and supported scope.
2. Module boundaries and owned data.
3. API and tenant boundary.
4. Domain lifecycle and key runtime flows.
5. Events and external dependencies.
6. Configuration and operations.
7. Tests, limitations, and related documentation.

Existing documents can move to this layout incrementally. Moving unrelated documentation is not a
requirement when adding or changing one feature.

## Getting started

- [Installation](instalation.md)
- [Architecture overview](architecture_overview.md)
- [Aspire local development](aspire-local-development.md)

## Architecture and engineering

- [Architecture history](architecture-history.md)
- [Engineering guidelines](engineering-guidelines.md)
- [Module boundaries](module-boundaries.md)
- [Multitenancy](multitenancy.md)
- [Envelope encryption operations](envelope-encryption.md)
- [Tenant quotas](quotas.md)
- [Workload placement](workload-placement.md)
- [Deployment targets](deployment-targets.md)
- [Demand-driven observation lifecycle](../src/shared/Observation/README.md)
- [Observability](observability.md)

## Feature documentation

- [Tenant membership and invitations](../src/features/Tenants/README.md)
- [Users and identity](../src/features/Users/README.md)

- [Web console and static frontend modules](../src/api/frontend/README.md)
- [Resource groups and resource registrations](../src/features/ResourceGroups/README.md)
- [Directed resource connections](../src/features/ResourceConnections/README.md)
- [Pole Functions](../src/features/Functions/README.md)
- [Compute profiles](../src/features/ComputeProfiles/README.md)
- [Service backups and PostgreSQL restore](../src/features/Backups/README.md)
- [PostgreSQL clusters](../src/features/Postgres/README.md)
- [Redis services](../src/features/RedisServices/README.md)
- [Tenant secrets](../src/features/Secrets/README.md)
- [Repository GitOps API and durable execution](../src/features/GitOps/README.md)
- [Web services](../src/features/WebServices/README.md)
- [Search](search.md) (existing central document; it can move beside the feature independently)
- [Private Access operations](private-access.md)

## Development and verification

- [Agent delivery and full local publication gate](agent-delivery.md)
- [Review checklist and evidence record](review-checklist.md)
- [Working metadata-feature starter](../templates/feature/README.md)
- [Testing](testing.md)
- [Feature test contract](feature-test-contract.md)
- [Local Kubernetes test lab](../infrastructure/kubernetes-lab/README.md)
- [Aspire end-to-end testing](aspire-e2e-testing.md)
- [GitHub source build end-to-end testing](github-source-build-e2e.md)

## SDKs

- [Pole Functions .NET SDK and Agent contract](https://github.com/vpole-cloud/vpole-functions)
