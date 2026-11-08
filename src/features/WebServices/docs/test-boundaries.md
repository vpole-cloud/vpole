# WebServices test boundaries

WebServices has an infrastructure-free suite in `WebServices.Tests`, a Mongo-backed suite in
`WebServices.Tests.Integration`, and cross-feature scenarios in `Pole.Tests.Integration`.
Live-cluster checks run in `Pole.Tests.Kubernetes`; full-stack scenarios run in `Pole.Tests.E2E`.

## Feature-owned checks

The infrastructure-free project owns domain transitions, serialization, manifests, fake Kubernetes
adapters and observation, quota profile definitions, update-log semantics, and declaration-provider
registration aliases and scoped lifetime.

The Mongo project owns schema validation, deployment-completion consumers, Create, list filtering
and pagination, tenant-scoped history lookup, stale update rejection, image authorization retention,
configuration no-op handling, and rollback specification rules. These tests read the actual
WebServices, releases, and outbox collections. The real ResourceGroups lookup, registrar, and
registration fence participate in the caller's Mongo transaction. `Tenants.Testing` and
`ResourceGroups.Testing` own preparation and registration assertions. Deployment-target discovery,
placement mutation responses, connection/Secret admission, GitOps status, and quota admission use
explicit Contracts substitutes when the scenario only needs their accepted outcome.

## Shared services scenarios

These classes remain in `Pole.Tests.Integration/WebServices` for the guarantees below.

| Class | Guarantee and real collaborators |
| --- | --- |
| `WebServiceIntegrationTests` | Exposure changes retain or remove actual Domains bindings according to applied ResourceDeployment generations. Add/remove/retry operations update physical placement inventory and aggregate releases atomically. Compute-profile changes preserve or adopt real stored revisions. Final deletion waits for placement cleanup and releases registrations and bindings. |
| `WebServiceMetadataUpdateTests` | A ResourceDeployments reconciliation claim between reading and updating fences physical configuration changes while allowing metadata changes. Both branches of the interleaving remain covered. |
| `WebServicePlacementQuotaTests` | Actual Quotas reservations and usage track desired placements, concurrent scaling, removal, retry, and transaction rollback. |
| `WebServiceLifecycleTests` | GitOps ownership and cleanup approvals remain stable across changes to Connections, Secrets, SourceBuilds, groups, scopes, and deployments. |
| `WebServiceSourceBuildDeploymentTests` | Promotion uses actual SourceBuilds job/configuration state, provenance, transactional promotion fences, and completion before persisting the WebService artifact and release. Placement writers are already explicit substitutes. |
| `WebServiceDeploymentApiTests` | Feature-specific HTTP binding, authorization, typed quota errors, release history, deployment-token use/revocation, tenant-header matching, and hostname routes. |
| `WebServiceSpecValidationApiTests` | The HTTP boundary rejects out-of-schema create/update payloads before persisted side effects and accepts valid probe boundaries and named ports. |

List and mutation scenarios moved from `WebServiceIntegrationTests`; metadata no-op and
changed-configuration scenarios moved from `WebServiceMetadataUpdateTests`. Migration preserves
all Theory inputs. Checks of physical collection contents in the retained scenarios continue to
exercise the real feature integrations. Feature-local tests inspect requests made through
Contracts where those requests are the WebServices decision under test.
