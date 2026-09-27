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
Domains does not reference WebServices or any other resource feature; ownership is represented by
`ResourceTypeId`, logical resource ID, and physical deployment ID.

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
