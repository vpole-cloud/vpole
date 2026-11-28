# Installation

vPole does not have a production installer yet. The only supported way to run it today is from
source with .NET Aspire. The control plane runs in containers on your machine, and it deploys
workloads into a Kubernetes cluster that you provide.

The Helm chart in `infrastructure/chart` is not an installer. It contains only the Let's Encrypt
`ClusterIssuer` that workloads use for HTTPS.

## How the pieces fit

| Part          | Runs where                      | Contains                                                  |
| ------------- | ------------------------------- | --------------------------------------------------------- |
| Control plane | Your machine, through Aspire    | vPole API, web console, MongoDB, Keycloak, RabbitMQ       |
| Workloads     | Your Kubernetes cluster         | Web services, databases, functions, their networking      |
| Connection    | Kubeconfig on the control plane | One context; vPole stores it encrypted on the first start |

## 1. Prepare your machine

- The .NET SDK version pinned in [`global.json`](../global.json).
- Node.js, then install the frontend dependencies once: `npm ci --prefix src/api/frontend`.
- The [Aspire CLI](https://aspire.dev).
- Docker or Podman. With Podman, set `ASPIRE_CONTAINER_RUNTIME=podman` for Aspire. Testcontainers
  and the test lab also need the Podman API socket
  (`systemctl --user enable --now podman.socket`) with `DOCKER_HOST` pointing at it.

## 2. Prepare the Kubernetes cluster

A single-node [k3s](https://k3s.io) cluster on Linux is the recommended starting point. Multi-node
clusters work too.

| Component                                                              | Why vPole needs it                                   | On k3s                              |
| ---------------------------------------------------------------------- | ---------------------------------------------------- | ----------------------------------- |
| Ingress controller                                                     | Public hostnames for web services (class `traefik`)  | Included (Traefik)                  |
| NetworkPolicy enforcement                                              | Default-deny isolation between tenants and resources | Included                            |
| Default storage class                                                  | Volumes for PostgreSQL and Redis                     | Included (`local-path`)             |
| [cert-manager](https://cert-manager.io)                                | TLS certificates for hostnames                       | Install                             |
| `letsencrypt-prod` `ClusterIssuer`                                     | The issuer that web service ingresses reference      | Install from `infrastructure/chart` |
| Prometheus Operator CRDs (`ServiceMonitor`)                            | Web services create a `ServiceMonitor`               | Install                             |
| [CloudNativePG](https://cloudnative-pg.io) and its Barman Cloud plugin | PostgreSQL clusters and their backups                | Install                             |

Install the issuer after cert-manager:

```sh
helm install pole-platform infrastructure/chart --set letsencrypt.email=you@example.com
```

The kubeconfig identity must be able to manage namespaces, NetworkPolicies and workload resources.
In a home lab, a cluster-admin context is the simplest choice. vPole checks these permissions when it
validates the connection.

The [Kubernetes test lab](../infrastructure/kubernetes-lab/README.md) builds disposable k3d clusters
with this whole component set. Use it as a reference for working versions, or to run the test
suites. It is not meant for real workloads.

## 3. Configure the network

These steps are only needed when workloads should be reachable from the internet.

1. **Domain.** Point a domain at your public IP, with a wildcard for generated hostnames:

    ```text
    A  apps.example.com    -> <public IP>
    A  *.apps.example.com  -> <public IP>
    ```

2. **Stable internal IP.** Give the server a static address or a DHCP reservation, so port
   forwarding keeps working after reboots.
3. **Port forwarding.** Forward TCP 80 and 443 from the router to the server. Let's Encrypt needs
   port 80 for HTTP-01 validation.
4. **Base domain.** Set the deployment target's public base domain to your domain. The default is
   `apps.localhost`. On the first start, the value comes from
   `DeploymentTargets:Bootstrap:WorkloadConfiguration:Ingress:PublicBaseDomain` in
   `src/api/Pole.Server/appsettings.json`, or from the matching environment variable.
5. **Ingress traffic into isolated namespaces.** Every workload namespace denies traffic by default.
   Declare the ingress controller as a network source, so that it can reach web services. See
   [deployment target bootstrap](deployment-targets.md#bootstrap) for the configuration and a
   Traefik example.

The bootstrap settings are read only while no deployment target exists. After the first start,
MongoDB is the source of truth. Change the target through the platform API instead.

## 4. Create the encryption key

vPole encrypts secrets and stored credentials with a key that you create and back up. It never
generates the key itself, and it refuses to start without one. Follow
[envelope encryption: bootstrap a key](envelope-encryption.md#bootstrap-a-key), then set the three
AppHost parameters described in
[Aspire local development](envelope-encryption.md#aspire-local-development).

Back up the key file. Without it, the encrypted data cannot be recovered.

## 5. Start vPole

Select the cluster as the current kubeconfig context, then run from the repository root:

```sh
aspire run --apphost src/api/Pole.AppHost/Pole.AppHost.csproj
```

On the first start, vPole connects to the current context and registers it as the default
deployment target. The web console runs at `http://localhost:5173`. The Aspire dashboard shows
every resource, its logs and its endpoints.

Development defaults for MongoDB, RabbitMQ and Keycloak credentials are in
`src/api/Pole.AppHost/appsettings.Development.json`. Override them with AppHost user secrets
before you expose anything beyond your machine.

## Optional features

- **Source builds** (GitHub → BuildKit → Harbor) need the explicit-start `pole-harbor` and
  `pole-buildkit` resources and a GitHub App. See [GitHub source build E2E](github-source-build-e2e.md).
- **Private access** lets the `pole` CLI reach tenant databases. vPole runs an Access Gateway in
  the cluster for this. See [Private Access operations](private-access.md).

## Next steps

- [Aspire local development](aspire-local-development.md): data resets, validation and
  troubleshooting.
- [Deployment targets](deployment-targets.md): adding more clusters.
- [Testing](testing.md): running the test suites.
