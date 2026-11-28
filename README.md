# vPole 🌱

> Some code namespaces and tools still use the project's former name, **Pole**.

**vPole** is a self-hosted control plane for running your own projects on your own hardware — a
step toward having **your own cloud provider at home**.

You describe _what_ to run — a web app, a database, a function — and vPole takes care of _how_ it
runs on Kubernetes: workloads, networking, hostnames, secrets, storage, backups and observation.
You work with applications, not clusters.

> Kubernetes should be an implementation detail, not a requirement.

## Status

🚧 **Early development.** vPole is not ready for production use yet. There is no installer, and
APIs, data schemas and resource formats still change without migration paths. Today it runs from
source through .NET Aspire against a Kubernetes cluster you provide.

## What vPole can do today

### Workloads

- **Web services** from a public container image or built from source: vPole clones a GitHub
  repository, builds it with BuildKit and publishes the image to a built-in Harbor registry. Each
  service keeps its release history and supports rollback, and it can run on several clusters at
  once.
- **PostgreSQL clusters** through CloudNativePG, with administrator credential rotation,
  scheduled backups to S3-compatible storage and restore.
- **Redis services** with persistent storage.
- **Functions**: small event-driven apps packaged as container images, with HTTP and timer
  triggers, immutable revisions and scale to zero. The [.NET SDK](https://github.com/vpole-cloud/vpole-functions)
  is published separately.

### Platform

- **Multitenancy**: tenants, email invitations and Owner/Admin/Member/Viewer roles; sign-in
  through Keycloak.
- **Isolation by default**: every isolation scope gets its own namespace in each cluster, with a
  default-deny network policy. A resource connection opens one exact path, such as a web service
  to its database.
- **Secrets**: stored encrypted (envelope encryption), bound explicitly to the resources that use
  them and projected into workloads; rotation without exposing values.
- **Domains**: generated hostnames for each workload and custom aliases in tenant-owned zones.
- **Compute and storage profiles** set CPU, memory, scheduling and storage classes, so tenants pick
  a profile instead of writing Kubernetes settings. **Quotas** limit what each tenant can use.
- **Multiple clusters**: register several Kubernetes clusters as deployment targets (in-cluster,
  kubeconfig or service account).
- **GitOps**: declare resources as YAML in GitHub repositories. vPole syncs them on push webhooks
  and polling, and repairs drift. Thirteen resource kinds are supported.
- **Private access**: reach a tenant database from your machine through short-lived grants,
  without exposing it publicly: `pole forward postgres/orders`.
- **Web console** with search and live updates.

## Roadmap

The main directions, tracked in [GitHub issues](https://github.com/vpole-cloud/vpole/issues):

- **Embedded single-instance mode** with minimal dependencies for small home labs: built-in
  authentication, embedded storage and in-process messaging instead of Keycloak, MongoDB and
  RabbitMQ ([#100](https://github.com/vpole-cloud/vpole/issues/100)).
- **A complete web console** for deployment and recovery
  ([#96](https://github.com/vpole-cloud/vpole/issues/96)) and a cluster onboarding wizard
  ([#11](https://github.com/vpole-cloud/vpole/issues/11)).
- **More managed services**: MongoDB ([#19](https://github.com/vpole-cloud/vpole/issues/19)) and
  S3-compatible object storage ([#20](https://github.com/vpole-cloud/vpole/issues/20)).
- **Operations**: an interactive terminal for workloads
  ([#61](https://github.com/vpole-cloud/vpole/issues/61)) and an audit log with a resource activity
  timeline ([#3](https://github.com/vpole-cloud/vpole/issues/3)).
- **Plugins**: third-party service types loaded at startup
  ([#53](https://github.com/vpole-cloud/vpole/issues/53)).

## What vPole is _not_

- ❌ Not a replacement for Kubernetes — it runs on top of a cluster you own.
- ❌ Not a managed cloud service — you run it, and your data stays with you.
- ❌ Not a PaaS locked to a vendor.

## Architecture

- **Backend**: a .NET modular monolith in C#, with some features in F#. Each feature owns its domain, persistence and API, and
  features talk through published contracts and integration events.
- **Storage and messaging**: MongoDB with a transactional outbox, plus RabbitMQ.
- **Identity**: Keycloak.
- **Web console**: React and Vite.
- **Local orchestration**: .NET Aspire composes the whole service graph.

See the [architecture overview](docs/architecture_overview.md) and
[module boundaries](docs/module-boundaries.md).

## Getting started (development)

You need:

- the .NET SDK pinned in [`global.json`](global.json), Node.js and the Aspire CLI;
- Docker or Podman;
- a Kubernetes cluster (for example, k3s) as the current context, for anything that deploys
  workloads;
- a local encryption key, set up once as described in
  [envelope encryption](docs/envelope-encryption.md#aspire-local-development).

Then start the full stack from the repository root:

```sh
aspire run --apphost src/api/Pole.AppHost/Pole.AppHost.csproj
```

The web console runs at `http://localhost:5173`. The
[Aspire local development guide](docs/aspire-local-development.md) covers optional resources,
data resets and troubleshooting, and [testing](docs/testing.md) describes the test suites.

## Documentation

Start with the [documentation index](docs/README.md). It links to architecture, engineering,
operations, testing and feature documentation.

## Contributing

Contributions are welcome. Read [CONTRIBUTING.md](CONTRIBUTING.md) first, and report security issues
as described in [SECURITY.md](SECURITY.md).

## License

vPole is licensed under the [GNU Affero General Public License v3.0 only](LICENSE.txt)
(`AGPL-3.0-only`). Commercial licenses are available from the maintainer on request.

Contributions require a signed [Contributor License Agreement](CLA.md); see
[CONTRIBUTING.md](CONTRIBUTING.md).
