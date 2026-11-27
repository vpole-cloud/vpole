# vPole 🌱

> Some code namespaces and tools still use the project's former name, **Pole**.

**vPole** is a self-hosted control plane for running your own projects in a home lab —\
a step toward having **your own cloud provider at home**.

The goal of vPole is to make deployment, management, and observability **simple and friendly**, without relying on public cloud providers and without requiring deep Kubernetes knowledge.

---

## Why vPole exists

Running services at home (or on your own servers) usually means:

- learning Kubernetes in depth
- writing YAMLs
- manually configuring ingress, secrets, networking
- gluing together observability tools
- maintaining everything yourself

vPole exists to **remove this complexity**.

You should be able to:

- deploy apps
- manage configuration
- expose services
- observe what’s happening

**without needing to understand Kubernetes at all.**

---

## What vPole is

vPole is:

- a **self-hosted platform**
- that manages a **Kubernetes cluster for you**
- through a **user-friendly API and UI**
- acting like a **personal cloud provider**

You tell vPole _what you want to run_ — vPole takes care of _how it runs_.

---

## Core ideas

- 🏠 **Self-hosted first** — designed for home labs and small private clusters
- ☁️ **Cloud-like experience** — without cloud providers
- 🧠 **Kubernetes under the hood** — invisible to the user
- 🛠 **Opinionated, but flexible**
- 🔐 **You own your data and infrastructure**

---

## Current stage (Stage 1)

vPole currently focuses on **web application deployment**.

### What is supported now

- Deployment of web apps using **public Docker images**
- Automatic creation and management of:
    - Deployments
    - ConfigMaps
    - Secrets
    - Ingress resources
- Kubernetes resources are managed **programmatically**, not via raw YAML
- Designed to run on **self-hosted Kubernetes clusters** (e.g. k3s)

### What is _not_ supported yet

- Private Docker registries
- GitHub / GitHub Actions integration
- Database provisioning
- Advanced networking policies

These are planned next.

---

## Roadmap

### Stage 2 — Databases

Easy, one-click deployment and management of common databases:

- MongoDB
- Redis
- PostgreSQL
- (and others)

Including:

- sane defaults
- persistent storage
- safe exposure options

### Stage 3 — Integrations

- Private image registries
- GitHub / CI integrations
- Automated deployments

---

## What vPole is _not_

- ❌ Not a replacement for Kubernetes
- ❌ Not a managed cloud service
- ❌ Not a PaaS locked to a vendor

vPole is a **local, self-hosted control plane** that makes Kubernetes usable without learning Kubernetes.

---

## Philosophy

> Kubernetes should be an implementation detail, not a requirement.

vPole treats Kubernetes as an internal engine —\
users interact with **applications**, not clusters.

---

## Status

🚧 **Early development / experimental**

The project is actively evolving and APIs may change.

---

## Documentation

Start with the [documentation index](docs/README.md). It links to installation, architecture,
engineering, operations, testing, and feature-owned documentation such as [vPole
Functions](src/features/Functions/README.md).

---

## License

vPole is licensed under the [GNU Affero General Public License v3.0 only](LICENSE.txt)
(`AGPL-3.0-only`). Commercial licenses are available from the maintainer on request.

Contributions require a signed [Contributor License Agreement](CLA.md); see
[CONTRIBUTING.md](CONTRIBUTING.md).
