# Domains

The Domains feature owns tenant DNS-zone reservations and the hostname bindings attached to exact
physical resource deployments. It records reservation and ingress intent; DNS-provider automation
and external-domain ownership verification are separate capabilities.

Each public deployment target supplies one non-overlapping public base domain. A tenant receives a
generated child zone per target and may reserve additional managed child zones. Every hostname is
globally unique, belongs to one zone and one exact `ResourceDeployment`, and is either generated or
custom. Generated hostnames cannot be removed independently. Custom aliases must remain below a
zone owned by the same tenant and deployment target.

Cross-feature callers use the narrow transactional capabilities in `Domains.Contracts`. They pass
their active Mongo session so hostname reservation, the physical ingress snapshot, the logical
resource revision, and the outbox-visible desired-state mutation commit or roll back together.
Domains does not reference workload application modules; ownership is represented by
`ResourceTypeId`, logical resource ID, and physical deployment ID.

The tenant-read `GET /api/v1/domain-zones/{id}/bindings` API returns a bounded page of reservations
with generic registered-resource names, group IDs and exact persisted deployment apply state.
Zone and binding reads are tenant-filtered; missing registration or placement remains visibly
unavailable. Provider-owned ResourceGroups and ResourceDeployments read contracts supply that
metadata without a workload-provider switch. Hostname conflicts disclose no other owner.

The browser separates zone catalog, reservation and ownership details from WebService target
hostname configuration. Custom-alias replacement uses the existing attach and detach operations
as two durable steps. It reserves the replacement first; a failed detach retains both aliases
and retries do not duplicate the replacement. Generated reservations remain provider-managed.
Reservation and Applied ingress state do not establish DNS, TLS or application reachability.

DNS syntax is implemented in the dependency-light `Pole.Dns` project. Input is normalized to
lowercase ASCII and may contain already encoded `xn--` labels. Unicode-to-punycode conversion,
external apex-domain verification, shared hostnames across targets, and DNS-provider record
creation are outside this feature's current scope.

## Operation outcomes

Zone, hostname-binding and cleanup capabilities return named outcome unions. Capability delegates
use operation names without an `Outcome` suffix; the corresponding result type carries that suffix.
Provider error groups remain available to workload consumers during their migration. Domain-zone
endpoints expose concrete local errors and call HTTP mapping methods directly. Only provider
contract error mappings remain registered for consumers still using the dynamic mapper.
