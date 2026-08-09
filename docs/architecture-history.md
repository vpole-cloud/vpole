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
