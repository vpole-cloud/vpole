# Architecture history

This document records significant development-stage architecture changes and their motivation.
It is not a release changelog; feature READMEs describe only the current supported design.

## Capability-oriented persistence

Feature handlers previously tended to receive broad database contexts. Redis introduced named
persistence capabilities, and the approach was subsequently applied to WebServices and
ComputeProfiles. A capability exposes one semantic operation, centralizes its tenant/revision
filters, and keeps aggregate persistence and outbox writes together without granting callers
access to the entire feature collection.

Cross-feature consumers depend on narrow contracts owned by the providing feature. Application
projects contain implementations and composition wiring, but are not used as integration-contract
assemblies.

The dependency direction was later formalized for every feature layer: domains remain internal,
synchronous integration uses provider-owned Contracts, serialized integration uses Events, and
shared concepts move only into small thematic primitives projects. The current normative rules are
documented in [Module boundaries](module-boundaries.md).

## Durable workload placement

Resource placement moved from logical resource specifications into `ResourceDeployment`.
ResourceDeployment owns the resolved target, runtime cell, namespace, storage, ingress, and
reconciliation lifecycle. Logical resources retain their desired application configuration and
join deployment mutations through the same Mongo transaction and outbox boundary.

## Portable compute profiles

ComputeProfile introduced a platform-managed catalog for CPU, memory, extended resources, and
Kubernetes scheduling policy. Resource create resolves a profile for the tenant, resource type,
and deployment target. ResourceDeployment stores the complete immutable resolved snapshot, so
later catalog edits do not silently alter existing workloads and reconciliation never depends on
mutable profile state.

Resolution and mutation are coordinated by revision/fence updates. Profile target mappings also
own transactional deployment-target references, and hard deletion is allowed only when no
ResourceDeployment snapshot still references the profile.

## DNS records became domain ownership

The original `DnsRecord` model combined a user-facing URL, an ingress rule, and a global
uniqueness reservation in one document. Multi-target placement made that model ambiguous because
the same logical WebService can have independent public endpoints in different clusters.

Pole replaced it with target-owned public base domains, tenant domain zones, and hostname
bindings attached to exact ResourceDeployments. Each public deployment receives a permanent
generated hostname below its target and tenant zone. Tenants may reserve child zones and add
custom aliases, while a global hostname index prevents conflicting ownership. ResourceDeployment
stores the ordered hostname snapshot used by reconciliation, so Kubernetes manifests are rendered
from immutable desired state rather than mutable DNS or target configuration.
