Here’s a **clear, practical installation section** you can drop straight into the README.
I’ve written it in a *“early but confident”* tone and structured it so you can expand it later when Helm arrives.

---

## Installation

Pole is designed to run in a **self-hosted environment**.
At the moment, the recommended way to run Pole is **locally via .NET Aspire**.
A Helm chart and cluster-native installation will be added later.

---

## Requirements

Before installing Pole, make sure the following requirements are met.

### 1. Kubernetes cluster

You need a working Kubernetes cluster.

* Recommended: **k3s on Linux**
* Single-node clusters are fully supported
* Multi-node clusters will work as well

> Pole manages Kubernetes for you, but Kubernetes must already exist.

---

### 2. Domain name

You need a domain name pointing to your home server.

Recommended DNS records:

* `A @     -> <your public static IP>`
* `A *     -> <your public static IP>`

This allows:

* root domain access
* wildcard subdomains for services managed by Pole

---

### 3. Static IP on the server

Your server PC must have a **stable internal IP address**.

You can achieve this by:

* configuring a static IP on the server itself, or
* assigning a DHCP reservation in your router

This is required so port forwarding does not break after reboots.

---

### 4. Port forwarding (router → server)

Incoming traffic must be forwarded from your router to the server running Kubernetes.

Minimum required ports:

* **80 → 80** (HTTP)
* **443 → 443** (HTTPS)

#### MikroTik example (NAT rules)

Forward HTTP:

```text
Chain: dstnat
Protocol: tcp
Dst. Port: 80
Action: dst-nat
To Address: <SERVER_LOCAL_IP>
To Port: 80
```

Forward HTTPS:

```text
Chain: dstnat
Protocol: tcp
Dst. Port: 443
Action: dst-nat
To Address: <SERVER_LOCAL_IP>
To Port: 443
```

Notes:

* Firewall rules must allow forwarded traffic
* Later, Pole will support VPN-only and allowlist-based exposure modes.

---

### 5. Local development with Aspire (current approach)

At the current stage, Pole is started using **.NET Aspire**.

Aspire:

* runs all required services in Docker
* wires dependencies automatically
* provides a simple local development experience

This is the **recommended way to start Pole today**.

Before the first start, bootstrap and back up the LocalKeyFile envelope-encryption key, then set the
required AppHost parameters as described in the
[envelope-encryption runbook](envelope-encryption.md#aspire-local-development). Pole deliberately
does not generate a key when the file is absent.

---

## Running Pole locally (development)

Prerequisites:

* .NET SDK
* Docker
* Kubernetes cluster accessible from your machine

Start Pole using Aspire:

```bash
dotnet run --project src/api/Pole.AppHost/Pole.AppHost.csproj
```

Aspire will:

* start all required containers
* configure service dependencies
* expose a local dashboard

No manual dependency-container setup is required. The operator-created envelope-encryption key
file remains a separate prerequisite and must not be committed or stored in normal appsettings.

---

## Future installation options

Planned:

* Helm chart installation
* Cluster-native deployment
* Private registry support
* CI/CD integrations

For now, Aspire provides the fastest and simplest way to get started.
