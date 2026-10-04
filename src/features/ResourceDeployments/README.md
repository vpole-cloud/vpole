# Resource deployments

ResourceDeployments owns persisted physical workload placement, reconciliation attempts and
current apply/delete outcomes. Domain stores the deployment state machine; Contracts exposes
provider extension points and tenant-safe placement summaries; Application owns API reads,
transactional state/outbox writes, recovery scheduling and adapter/observer catalogs. Resource
providers own logical configuration and native object selection. RuntimeCells owns namespaces;
DeploymentTargets owns clients and admission. This feature does not store credentials or audit history.


## Tenant operations and runtime observation

`GET /resource-deployments/operations` pages current persisted placements in the active tenant,
optionally by resource group or exact resource type and ID. State totals cover the whole filtered
set; pod observations cover the selected page. Waiting, Accepted, Running, Applied and Failed
remain reconciliation phases, with operation/desired generations, attempts, transition time and
retry eligibility. This is current durable state, not an audit history. Closing a browser view
stops observation without cancelling server work. Builds and releases retain their own feature
state and are displayed separately in the WebService Operations section.

`GET /resource-deployments/{deploymentId}/observation` resolves only that tenant-owned persisted
placement. Providers contribute `IResourceDeploymentObserver` through a validated descriptor
catalog; the platform never switches on built-in resource types. WebServices select Deployments,
Redis StatefulSets, and PostgreSQL its configured StatefulSet or CloudNativePG Cluster.
The operator signal checks the Ready condition and its observed generation using the
[CloudNativePG API](https://cloudnative-pg.io/docs/1.27/cloudnative-pg.v1/).
Applied confirms an adapter write, not application health. Ready requires the current Pole and
native rollout, complete desired replica counts and current owned Ready pods. Redis and native
PostgreSQL currently lack readiness probes; their signal is pod readiness, not a database query.

Reads use the persisted target and namespace, full workload identity labels and verified controller
UID ownership (including the Deployment -> ReplicaSet -> Pod chain). PostgreSQL uses its existing
provider-owned native label values. Results recheck the persisted deployment revision after native
reads; replaced evidence returns Superseded. Observations have a 15-second budget, four concurrent
requests, at most 100 pods and 32 warnings across eight verified UIDs. Limited pod results cannot
prove complete readiness. Missing workloads, unavailable targets, forbidden reads and timeouts are
explicit; warning-read failure can coexist with usable pod observations. Responses contain bounded
pod phases/readiness/restarts and allowlisted reasons/counts/times, never raw messages, conditions,
workload specifications, commands, environment, credential material or native exception bodies.

Generic retry requires `expectedRevision` and `expectedOperation` query values and preserves the
existing tenant write check and atomic state/outbox transaction. Stale selections return conflict.
WebServices use their owner retry endpoint to preserve release bookkeeping. Tenant/group summaries
link to resources; supported precise retries appear in the owner's Operations section. Browser
reads clear on tenant/token/API scope changes, refresh on reconnect and explicitly mark stale
observations. The API is authoritative for Viewer versus writer access.
