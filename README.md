# Pole 🌱

**Pole** is a self-hosted control plane for running your own projects in a home lab —  
a step toward having **your own cloud provider at home**.

The goal of Pole is to make deployment, management, and observability **simple and friendly**, without relying on public cloud providers and without requiring deep Kubernetes knowledge.

---

## Why Pole exists

Running services at home (or on your own servers) usually means:
- learning Kubernetes in depth
- writing YAMLs
- manually configuring ingress, secrets, networking
- gluing together observability tools
- maintaining everything yourself

Pole exists to **remove this complexity**.

You should be able to:
- deploy apps
- manage configuration
- expose services
- observe what’s happening

**without needing to understand Kubernetes at all.**

---

## What Pole is

Pole is:
- a **self-hosted platform**
- that manages a **Kubernetes cluster for you**
- through a **user-friendly API and UI**
- acting like a **personal cloud provider**

You tell Pole *what you want to run* — Pole takes care of *how it runs*.

---

## Core ideas

- 🏠 **Self-hosted first** — designed for home labs and small private clusters
- ☁️ **Cloud-like experience** — without cloud providers
- 🧠 **Kubernetes under the hood** — invisible to the user
- 🛠 **Opinionated, but flexible**
- 🔐 **You own your data and infrastructure**

---

## Current stage (Stage 1)

Pole currently focuses on **web application deployment**.

### What is supported now

- Deployment of web apps using **public Docker images**
- Automatic creation and management of:
  - Deployments
  - ConfigMaps
  - Secrets
  - Ingress resources
- Kubernetes resources are managed **programmatically**, not via raw YAML
- Designed to run on **self-hosted Kubernetes clusters** (e.g. k3s)

### What is *not* supported yet

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

## What Pole is *not*

- ❌ Not a replacement for Kubernetes
- ❌ Not a managed cloud service
- ❌ Not a PaaS locked to a vendor

Pole is a **local, self-hosted control plane** that makes Kubernetes usable without learning Kubernetes.

---

## Philosophy

> Kubernetes should be an implementation detail, not a requirement.

Pole treats Kubernetes as an internal engine —  
users interact with **applications**, not clusters.

---

## Status

🚧 **Early development / experimental**

The project is actively evolving and APIs may change.

---

## Installation
See the [installation guide](docs/instalation.md) for instructions on how to set up Pole in your environment.

---

## Architecture
See the [architecture overview](docs/architecture_overview.md) for details about the tech stack and design principles behind Pole.

---

## Testing
See the [testing notes](docs/testing.md) for information about the current backend test setup, requirements, and how to run the integration suite.

---

## License

[MIT License](LICENSE.txt)
