# FeatureName

This generated starter is a tenant-owned metadata feature. Before delivery, replace this
introduction with its actual purpose, supported scope, limitations and related documentation.

- Domain owns the stored resource, details/audit values and factory/transition rules.
- Application owns Create/Get/Update HTTP boundaries, named persistence capabilities, Mongo filters
  and atomic resource/outbox writes.
- Events owns its serialized created/updated facts and tenant metadata.
- Migrations owns frozen schema/index definitions and is registered separately at the host.
- Contracts is not needed until a real cross-feature synchronous operation exists.

API: `/api/v1/feature-route`, with the existing tenant header and authorization pipeline.
Create accepts name/description. Update requires expected revision plus details. Names are trimmed,
case-sensitive and tenant-unique. A no-op preserves business revision, timestamps and outbox.

Finish the composition steps in [the starter guide](../../../templates/feature/README.md).
Follow [engineering rules](../../../docs/engineering-guidelines.md),
[module boundaries](../../../docs/module-boundaries.md) and
[the review checklist](../../../docs/review-checklist.md).

Run the local `FeatureName.Tests` and `FeatureName.Tests.Integration` projects after a solution build.
The integration suite uses the shared real Mongo replica-set/API host and current owner migration.
Document the actual behavior and run the required publication checks before a PR.
