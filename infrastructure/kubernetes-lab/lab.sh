#!/usr/bin/env bash
set -Eeuo pipefail

LAB_SCRIPT_DIR="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd -P)"
LAB_REPOSITORY_ROOT="$(cd -- "${LAB_SCRIPT_DIR}/../.." && pwd -P)"
LAB_GIT_COMMON_DIR="$(git -C "${LAB_REPOSITORY_ROOT}" rev-parse --path-format=absolute --git-common-dir 2>/dev/null || true)"
if [[ -n "${LAB_GIT_COMMON_DIR}" && "$(basename -- "${LAB_GIT_COMMON_DIR}")" == .git ]]; then
    LAB_SHARED_REPOSITORY_ROOT="$(dirname -- "${LAB_GIT_COMMON_DIR}")"
else
    LAB_SHARED_REPOSITORY_ROOT="${LAB_REPOSITORY_ROOT}"
fi
LAB_ALLOWED_STATE_ROOT="${LAB_SHARED_REPOSITORY_ROOT}/.tmp/kubernetes-lab"
LAB_STATE_ROOT="${POLE_K8S_LAB_STATE_DIR:-${LAB_ALLOWED_STATE_ROOT}}"
LAB_STATE_FILE="${LAB_STATE_ROOT}/state.json"
LAB_KUBECONFIG_DIR="${LAB_STATE_ROOT}/kubeconfigs"
LAB_BIN_DIR="${LAB_STATE_ROOT}/bin"
LAB_REGISTRY_NAME="pole-lab-registry"
LAB_REGISTRY_CONTAINER="k3d-${LAB_REGISTRY_NAME}"
LAB_NETWORK_NAME="pole-lab"
LAB_INSTANCE_LABEL="pole.dev/lab-instance-id"
LAB_K3D_KUBECONFIG_SINK="${LAB_STATE_ROOT}/k3d-kubeconfig-sink.yaml"
LAB_REGISTRIES_CONFIG="${LAB_STATE_ROOT}/registries.yaml"
LAB_STATE_SCHEMA_VERSION=2

# shellcheck source=versions.env
source "${LAB_SCRIPT_DIR}/versions.env"

K3D_BIN=""
KUBECTL_BIN=""
HELM_BIN=""
BUILDCTL_BIN=""
CONTAINER_RUNTIME=""
CONTAINER_CLI=""
CONTAINER_RUNTIME_ROOTLESS=false
LAB_INSTANCE_ID=""
LAB_REGISTRY_CONTAINER_ID=""
PROFILE=""
PROFILE_TARGET_KEYS=()
PROFILE_CLUSTER_NAMES=()
PROFILE_API_PORTS=()
PROFILE_HTTP_PORTS=()
PROFILE_HTTPS_PORTS=()
PROFILE_AGENT_COUNTS=()
PROFILE_OVERLAYS=()
VERIFY_TARGET_INDEXES=()
VERIFY_NAMESPACE_NAMES=()
VERIFY_RUN_ID=""
VERIFY_BUILDKIT_FORWARD_PID=""
VERIFY_BUILDKIT_FORWARD_LOG=""
VERIFY_HARBOR_PROJECT=""
VERIFY_HARBOR_IMAGE=""

usage() {
    cat <<'EOF'
Usage:
  ./infrastructure/kubernetes-lab/lab.sh up [full|minimal|multi|topology] [--overlay NAME]...
  ./infrastructure/kubernetes-lab/lab.sh status
  ./infrastructure/kubernetes-lab/lab.sh verify
  ./infrastructure/kubernetes-lab/lab.sh audit-tests
  ./infrastructure/kubernetes-lab/lab.sh clean-tests
  ./infrastructure/kubernetes-lab/lab.sh reset [full|minimal|multi|topology] [--overlay NAME]...
  ./infrastructure/kubernetes-lab/lab.sh down

The lab writes only beneath .tmp/kubernetes-lab, never updates the default
kubeconfig, and never switches the user's current context.

The default full profile installs every required overlay on the default target and
monitoring on both workload targets. Focused profiles accept optional overlays:
monitoring, cert-manager, cnpg. Harbor and BuildKit belong to the full profile.
EOF
}

info() {
    printf '==> %s\n' "$*"
}

warn() {
    printf 'WARN: %s\n' "$*" >&2
}

die() {
    printf 'ERROR: %s\n' "$*" >&2
    exit 1
}

need() {
    command -v "$1" >/dev/null 2>&1 || die "Required command '$1' was not found."
}

generate_lab_instance_id() {
    [[ -r /proc/sys/kernel/random/uuid ]] \
        || die "The kernel UUID source '/proc/sys/kernel/random/uuid' is unavailable."
    tr --delete '-' </proc/sys/kernel/random/uuid
}

validate_state_root() {
    need realpath
    local resolved_allowed_root
    local resolved_state_root
    resolved_allowed_root="$(realpath -m -- "${LAB_ALLOWED_STATE_ROOT}")"
    resolved_state_root="$(realpath -m -- "${LAB_STATE_ROOT}")"

    case "${resolved_state_root}" in
        "${resolved_allowed_root}"|"${resolved_allowed_root}/"*) ;;
        *) die "Lab state must remain beneath '${resolved_allowed_root}', got '${resolved_state_root}'." ;;
    esac

    LAB_STATE_ROOT="${resolved_state_root}"
    LAB_STATE_FILE="${LAB_STATE_ROOT}/state.json"
    LAB_KUBECONFIG_DIR="${LAB_STATE_ROOT}/kubeconfigs"
    LAB_BIN_DIR="${LAB_STATE_ROOT}/bin"
    LAB_K3D_KUBECONFIG_SINK="${LAB_STATE_ROOT}/k3d-kubeconfig-sink.yaml"
    LAB_REGISTRIES_CONFIG="${LAB_STATE_ROOT}/registries.yaml"
}

load_profile() {
    PROFILE="$1"
    case "${PROFILE}" in
        full)
            PROFILE_TARGET_KEYS=(default workload-a workload-b)
            PROFILE_CLUSTER_NAMES=(pole-lab-default pole-lab-workload-a pole-lab-workload-b)
            PROFILE_API_PORTS=(6552 6553 6554)
            PROFILE_HTTP_PORTS=(8089 8090 8091)
            PROFILE_HTTPS_PORTS=(8449 8450 8451)
            PROFILE_AGENT_COUNTS=(3 0 0)
            ;;
        minimal)
            PROFILE_TARGET_KEYS=(default)
            PROFILE_CLUSTER_NAMES=(pole-lab-minimal)
            PROFILE_API_PORTS=(6551)
            PROFILE_HTTP_PORTS=(8088)
            PROFILE_HTTPS_PORTS=(8448)
            PROFILE_AGENT_COUNTS=(0)
            ;;
        multi)
            PROFILE_TARGET_KEYS=(default workload-a workload-b)
            PROFILE_CLUSTER_NAMES=(pole-lab-default pole-lab-workload-a pole-lab-workload-b)
            PROFILE_API_PORTS=(6552 6553 6554)
            PROFILE_HTTP_PORTS=(8089 8090 8091)
            PROFILE_HTTPS_PORTS=(8449 8450 8451)
            PROFILE_AGENT_COUNTS=(0 0 0)
            ;;
        topology)
            PROFILE_TARGET_KEYS=(default)
            PROFILE_CLUSTER_NAMES=(pole-lab-topology)
            PROFILE_API_PORTS=(6555)
            PROFILE_HTTP_PORTS=(8092)
            PROFILE_HTTPS_PORTS=(8452)
            PROFILE_AGENT_COUNTS=(3)
            ;;
        *) die "Unknown profile '${PROFILE}'. Expected full, minimal, multi, or topology." ;;
    esac
}

parse_profile_and_overlays() {
    local requested_profile=full
    local profile_seen=false
    local -a requested_overlays=()
    while [[ "$#" -gt 0 ]]; do
        case "$1" in
            full|minimal|multi|topology)
                [[ "${profile_seen}" == false ]] || die "Only one lab profile may be selected."
                requested_profile="$1"
                profile_seen=true
                shift
                ;;
            --overlay)
                [[ "$#" -ge 2 ]] || die "--overlay requires a name."
                requested_overlays+=("$2")
                shift 2
                ;;
            *) die "Unknown lab argument '$1'." ;;
        esac
    done

    load_profile "${requested_profile}"
    PROFILE_OVERLAYS=()
    if [[ "${PROFILE}" == full ]]; then
        [[ "${#requested_overlays[@]}" -eq 0 ]] \
            || die "The full profile has a fixed target-scoped overlay contract; do not pass --overlay."
        PROFILE_OVERLAYS=(monitoring cert-manager cnpg harbor buildkit)
        return
    fi
    local candidate
    for candidate in monitoring cert-manager cnpg harbor buildkit; do
        local requested
        for requested in "${requested_overlays[@]}"; do
            [[ "${requested}" == "${candidate}" ]] && PROFILE_OVERLAYS+=("${candidate}")
        done
    done
    local requested
    for requested in "${requested_overlays[@]}"; do
        case "${requested}" in
            monitoring|cert-manager|cnpg) ;;
            harbor|buildkit) die "Overlay '${requested}' is part of the fixed full profile and is not available on focused profiles." ;;
            *) die "Unknown overlay '${requested}'." ;;
        esac
        local matches=0
        for candidate in "${requested_overlays[@]}"; do
            [[ "${candidate}" == "${requested}" ]] && matches=$((matches + 1))
        done
        [[ "${matches}" -eq 1 ]] || die "Overlay '${requested}' was selected more than once."
    done
}

target_has_overlay() {
    local index="$1"
    local requested_overlay="$2"
    if [[ "${PROFILE}" == full ]]; then
        if [[ "${requested_overlay}" == monitoring ]]; then
            return 0
        fi
        [[ "${index}" -eq 0 ]] || return 1
    fi

    local overlay
    for overlay in "${PROFILE_OVERLAYS[@]}"; do
        [[ "${overlay}" == "${requested_overlay}" ]] && return 0
    done
    return 1
}

podman_socket_path() {
    local socket_path
    socket_path="$(podman info --format '{{.Host.RemoteSocket.Path}}')"
    [[ -n "${socket_path}" ]] || die "Podman did not report a Docker-compatible API socket path."
    printf '%s' "${socket_path}"
}

configure_podman_runtime() {
    need podman
    if [[ -n "${POLE_K8S_LAB_DOCKER_HOST:-}" || -n "${POLE_K8S_LAB_DOCKER_SOCK:-}" ]]; then
        [[ -n "${POLE_K8S_LAB_DOCKER_HOST:-}" && -n "${POLE_K8S_LAB_DOCKER_SOCK:-}" ]] \
            || die "POLE_K8S_LAB_DOCKER_HOST and POLE_K8S_LAB_DOCKER_SOCK must be set together."
        export DOCKER_HOST="${POLE_K8S_LAB_DOCKER_HOST}"
        export DOCKER_SOCK="${POLE_K8S_LAB_DOCKER_SOCK}"
    else
        local socket_path
        socket_path="$(podman_socket_path)"
        [[ -S "${socket_path}" ]] || die "Podman API socket '${socket_path}' is not active. Run 'systemctl --user enable --now podman.socket'."
        export DOCKER_HOST="unix://${socket_path}"
        export DOCKER_SOCK="${socket_path}"
    fi

    [[ "${DOCKER_HOST}" == "unix://${DOCKER_SOCK}" && "${DOCKER_SOCK}" == /* && -S "${DOCKER_SOCK}" ]] \
        || die "Podman overrides must identify the same active local Unix socket in POLE_K8S_LAB_DOCKER_HOST and POLE_K8S_LAB_DOCKER_SOCK."

    CONTAINER_RUNTIME=podman
    CONTAINER_CLI=podman
    CONTAINER_RUNTIME_ROOTLESS="$(podman info --format '{{.Host.Security.Rootless}}')"
}

configure_docker_runtime() {
    need docker
    if [[ -n "${POLE_K8S_LAB_DOCKER_HOST:-}" || -n "${POLE_K8S_LAB_DOCKER_SOCK:-}" ]]; then
        [[ -n "${POLE_K8S_LAB_DOCKER_HOST:-}" && -n "${POLE_K8S_LAB_DOCKER_SOCK:-}" ]] \
            || die "POLE_K8S_LAB_DOCKER_HOST and POLE_K8S_LAB_DOCKER_SOCK must be set together."
        export DOCKER_HOST="${POLE_K8S_LAB_DOCKER_HOST}"
        export DOCKER_SOCK="${POLE_K8S_LAB_DOCKER_SOCK}"
    fi
    if [[ -z "${DOCKER_SOCK:-}" && "${DOCKER_HOST:-}" == unix:///* ]]; then
        export DOCKER_SOCK="${DOCKER_HOST#unix://}"
    fi
    docker info >/dev/null 2>&1 || die "Docker is installed but its daemon is unavailable."

    # A Docker-compatible CLI can still be connected to Podman's API. Classify the
    # daemon by its local socket so state, Aspire, and Testcontainers use Podman rules.
    if [[ -n "${DOCKER_SOCK:-}" && -S "${DOCKER_SOCK}" ]] && command -v podman >/dev/null 2>&1; then
        local podman_socket
        podman_socket="$(podman_socket_path)"
        if [[ "${DOCKER_SOCK}" == "${podman_socket}" ]]; then
            CONTAINER_RUNTIME=podman
            CONTAINER_CLI=podman
            CONTAINER_RUNTIME_ROOTLESS="$(podman info --format '{{.Host.Security.Rootless}}')"
            export DOCKER_HOST="unix://${podman_socket}"
            export DOCKER_SOCK="${podman_socket}"
            return
        fi
    fi

    CONTAINER_RUNTIME=docker
    CONTAINER_CLI=docker
    CONTAINER_RUNTIME_ROOTLESS=false
}

podman_runtime_available() {
    command -v podman >/dev/null 2>&1 || return 1
    podman info >/dev/null 2>&1 || return 1
    if [[ -n "${POLE_K8S_LAB_DOCKER_HOST:-}" || -n "${POLE_K8S_LAB_DOCKER_SOCK:-}" ]]; then
        [[ -n "${POLE_K8S_LAB_DOCKER_HOST:-}" && -n "${POLE_K8S_LAB_DOCKER_SOCK:-}" ]]
        return
    fi
    local socket_path
    socket_path="$(podman info --format '{{.Host.RemoteSocket.Path}}' 2>/dev/null)"
    [[ -n "${socket_path}" && -S "${socket_path}" ]]
}

docker_runtime_available() {
    command -v docker >/dev/null 2>&1 && docker info >/dev/null 2>&1
}

select_container_runtime() {
    if [[ -n "${POLE_K8S_LAB_DOCKER_HOST:-}" || -n "${POLE_K8S_LAB_DOCKER_SOCK:-}" ]]; then
        [[ -n "${POLE_K8S_LAB_DOCKER_HOST:-}" && -n "${POLE_K8S_LAB_DOCKER_SOCK:-}" ]] \
            || die "POLE_K8S_LAB_DOCKER_HOST and POLE_K8S_LAB_DOCKER_SOCK must be set together."
    fi
    local requested="${POLE_K8S_LAB_RUNTIME:-auto}"
    case "${requested}" in
        auto|podman|docker) ;;
        *) die "POLE_K8S_LAB_RUNTIME must be auto, podman, or docker." ;;
    esac

    local state_runtime=""
    if [[ -f "${LAB_STATE_FILE}" ]]; then
        state_runtime="$(jq --raw-output '.runtime.name // empty' "${LAB_STATE_FILE}" 2>/dev/null || true)"
        if [[ -n "${state_runtime}" && "${requested}" != auto && "${requested}" != "${state_runtime}" ]]; then
            die "The active lab uses '${state_runtime}', but POLE_K8S_LAB_RUNTIME requested '${requested}'."
        fi
        [[ -z "${state_runtime}" || "${state_runtime}" == podman || "${state_runtime}" == docker ]] \
            || die "Lab state contains unsupported container runtime '${state_runtime}'."
    fi

    if [[ "${requested}" == auto && -n "${state_runtime}" ]]; then
        requested="${state_runtime}"
    fi
    if [[ "${requested}" == auto ]]; then
        if podman_runtime_available; then
            requested=podman
        elif docker_runtime_available; then
            requested=docker
        else
            die "No usable container runtime was found. Start Podman's API socket or Docker."
        fi
    fi

    case "${requested}" in
        podman) configure_podman_runtime ;;
        docker) configure_docker_runtime ;;
    esac
}

ensure_rootless_cgroup_delegation() {
    [[ "${CONTAINER_RUNTIME}" == podman && "${CONTAINER_RUNTIME_ROOTLESS}" == true ]] || return 0
    [[ "$(podman info --format '{{.Host.CgroupsVersion}}')" == v2 ]] \
        || die "The rootless Podman lab requires cgroup v2."

    local user_service_controllers="/sys/fs/cgroup/user.slice/user-$(id -u).slice/user@$(id -u).service/cgroup.controllers"
    [[ -f "${user_service_controllers}" ]] \
        || die "Could not inspect rootless Podman's delegated cgroup controllers."
    local controller
    for controller in cpu cpuset io memory pids; do
        grep --word-regexp --quiet "${controller}" "${user_service_controllers}" \
            || die "Rootless Podman is missing delegated '${controller}' cgroup control. The k3d Podman guide recommends stopping an active xdg-document-portal.service when it holds cpuset, or configuring the user@.service delegation as an administrator, then starting a new user session."
    done
}

ensure_rootless_inotify_capacity() {
    [[ "${PROFILE}" == full && "${CONTAINER_RUNTIME}" == podman && "${CONTAINER_RUNTIME_ROOTLESS}" == true ]] || return 0
    local instances_path=/proc/sys/fs/inotify/max_user_instances
    [[ -r "${instances_path}" ]] || die "Could not inspect the host inotify instance limit."
    local instances
    instances="$(<"${instances_path}")"
    [[ "${instances}" =~ ^[0-9]+$ ]] || die "The host inotify instance limit is malformed."
    [[ "${instances}" -ge 512 ]] || die "Rootless Podman needs fs.inotify.max_user_instances >= 512 for the full k3d lab (current: ${instances}). Set it temporarily with 'sudo sysctl -w fs.inotify.max_user_instances=1024' or persist an equivalent sysctl.d setting, then retry."
}

run_container() {
    "${CONTAINER_CLI}" "$@"
}

tool_architecture() {
    case "$(uname -m)" in
        x86_64|amd64) printf amd64 ;;
        aarch64|arm64) printf arm64 ;;
        *) die "Pinned lab tooling is not available for architecture '$(uname -m)'." ;;
    esac
}

ensure_pinned_tool() {
    local tool_name="$1"
    local version="$2"
    local architecture="$3"
    local expected_sha256="$4"
    local download_url="$5"
    local destination="${LAB_BIN_DIR}/${tool_name}-${version}-${architecture}"

    need curl
    need sha256sum
    mkdir -p -- "${LAB_BIN_DIR}"

    if [[ ! -x "${destination}" ]] || ! printf '%s  %s\n' "${expected_sha256}" "${destination}" | sha256sum --check --status; then
        local temporary_binary="${destination}.download"
        info "Downloading pinned ${tool_name} ${version} for linux/${architecture}" >&2
        curl --fail --location --silent --show-error "${download_url}" --output "${temporary_binary}"
        printf '%s  %s\n' "${expected_sha256}" "${temporary_binary}" | sha256sum --check --status \
            || die "Downloaded ${tool_name} binary failed its pinned SHA-256 check."
        chmod 0755 -- "${temporary_binary}"
        mv -- "${temporary_binary}" "${destination}"
    fi

    printf '%s' "${destination}"
}

ensure_pinned_buildctl() {
    local architecture="$1"
    local expected_sha256="$2"
    local archive="${LAB_BIN_DIR}/buildkit-${BUILDCTL_VERSION}-${architecture}.tar.gz"
    local destination="${LAB_BIN_DIR}/buildctl-${BUILDCTL_VERSION}-${architecture}"
    local download_url="https://github.com/moby/buildkit/releases/download/${BUILDCTL_VERSION}/buildkit-${BUILDCTL_VERSION}.linux-${architecture}.tar.gz"

    need curl
    need sha256sum
    need tar
    mkdir -p -- "${LAB_BIN_DIR}"
    if [[ ! -f "${archive}" ]] || ! printf '%s  %s\n' "${expected_sha256}" "${archive}" | sha256sum --check --status; then
        local temporary_archive="${archive}.download"
        info "Downloading pinned buildctl ${BUILDCTL_VERSION} for linux/${architecture}" >&2
        curl --fail --location --silent --show-error "${download_url}" --output "${temporary_archive}"
        printf '%s  %s\n' "${expected_sha256}" "${temporary_archive}" | sha256sum --check --status \
            || die "Downloaded BuildKit archive failed its pinned SHA-256 check."
        chmod 0600 -- "${temporary_archive}"
        mv -- "${temporary_archive}" "${archive}"
    fi

    if [[ ! -x "${destination}" ]]; then
        local extraction_root
        extraction_root="$(mktemp --directory "${LAB_STATE_ROOT}/buildctl-extract.XXXXXX")"
        tar --extract --gzip --file "${archive}" --directory "${extraction_root}" bin/buildctl
        [[ -f "${extraction_root}/bin/buildctl" && ! -L "${extraction_root}/bin/buildctl" ]] \
            || die "Pinned BuildKit archive did not contain a regular bin/buildctl file."
        chmod 0755 -- "${extraction_root}/bin/buildctl"
        mv -- "${extraction_root}/bin/buildctl" "${destination}"
        rmdir -- "${extraction_root}/bin" "${extraction_root}"
    fi

    printf '%s' "${destination}"
}

ensure_pinned_helm() {
    local architecture="$1"
    local expected_sha256="$2"
    local archive="${LAB_BIN_DIR}/helm-${HELM_VERSION}-${architecture}.tar.gz"
    local destination="${LAB_BIN_DIR}/helm-${HELM_VERSION}-${architecture}"
    local download_url="https://get.helm.sh/helm-${HELM_VERSION}-linux-${architecture}.tar.gz"

    need curl
    need sha256sum
    need tar
    mkdir -p -- "${LAB_BIN_DIR}"
    if [[ ! -f "${archive}" ]] || ! printf '%s  %s\n' "${expected_sha256}" "${archive}" | sha256sum --check --status; then
        local temporary_archive="${archive}.download"
        info "Downloading pinned Helm ${HELM_VERSION} for linux/${architecture}" >&2
        curl --fail --location --silent --show-error "${download_url}" --output "${temporary_archive}"
        printf '%s  %s\n' "${expected_sha256}" "${temporary_archive}" | sha256sum --check --status \
            || die "Downloaded Helm archive failed its pinned SHA-256 check."
        chmod 0600 -- "${temporary_archive}"
        mv -- "${temporary_archive}" "${archive}"
    fi

    if [[ ! -x "${destination}" ]]; then
        local extraction_root
        extraction_root="$(mktemp --directory "${LAB_STATE_ROOT}/helm-extract.XXXXXX")"
        tar --extract --gzip --file "${archive}" --directory "${extraction_root}" "linux-${architecture}/helm"
        [[ -f "${extraction_root}/linux-${architecture}/helm" && ! -L "${extraction_root}/linux-${architecture}/helm" ]] \
            || die "Pinned Helm archive did not contain a regular helm binary."
        chmod 0755 -- "${extraction_root}/linux-${architecture}/helm"
        mv -- "${extraction_root}/linux-${architecture}/helm" "${destination}"
        rmdir -- "${extraction_root}/linux-${architecture}" "${extraction_root}"
    fi

    printf '%s' "${destination}"
}

ensure_tooling() {
    local architecture
    architecture="$(tool_architecture)"
    local k3d_sha256
    local kubectl_sha256
    local helm_sha256
    local buildctl_sha256
    if [[ "${architecture}" == amd64 ]]; then
        k3d_sha256="${K3D_LINUX_AMD64_SHA256}"
        kubectl_sha256="${KUBECTL_LINUX_AMD64_SHA256}"
        helm_sha256="${HELM_LINUX_AMD64_SHA256}"
        buildctl_sha256="${BUILDCTL_LINUX_AMD64_SHA256}"
    else
        k3d_sha256="${K3D_LINUX_ARM64_SHA256}"
        kubectl_sha256="${KUBECTL_LINUX_ARM64_SHA256}"
        helm_sha256="${HELM_LINUX_ARM64_SHA256}"
        buildctl_sha256="${BUILDCTL_LINUX_ARM64_SHA256}"
    fi

    K3D_BIN="$(ensure_pinned_tool \
        k3d "${K3D_VERSION}" "${architecture}" "${k3d_sha256}" \
        "https://github.com/k3d-io/k3d/releases/download/${K3D_VERSION}/k3d-linux-${architecture}")"
    KUBECTL_BIN="$(ensure_pinned_tool \
        kubectl "${KUBECTL_VERSION}" "${architecture}" "${kubectl_sha256}" \
        "https://dl.k8s.io/release/${KUBECTL_VERSION}/bin/linux/${architecture}/kubectl")"
    HELM_BIN="$(ensure_pinned_helm "${architecture}" "${helm_sha256}")"
    BUILDCTL_BIN="$(ensure_pinned_buildctl "${architecture}" "${buildctl_sha256}")"

    local reported_version
    reported_version="$(${K3D_BIN} version | awk '/k3d version/ { version = $3 } END { print version }')"
    [[ "${reported_version}" == "${K3D_VERSION}" ]] \
        || die "Expected k3d ${K3D_VERSION}, got '${reported_version:-unknown}'."
    [[ "$(${KUBECTL_BIN} version --client --output json | jq --raw-output '.clientVersion.gitVersion')" == "${KUBECTL_VERSION}" ]] \
        || die "Pinned kubectl did not report expected version ${KUBECTL_VERSION}."
    [[ "$(${HELM_BIN} version --short)" == "${HELM_VERSION}"* ]] \
        || die "Pinned Helm did not report expected version ${HELM_VERSION}."
    local buildctl_version
    buildctl_version="$("${BUILDCTL_BIN}" --version)"
    [[ "${buildctl_version}" == *"${BUILDCTL_VERSION}"* ]] \
        || die "Pinned buildctl did not report expected version ${BUILDCTL_VERSION}."
}

ensure_k3d_kubeconfig_sink() {
    mkdir -p -- "${LAB_STATE_ROOT}"
    if [[ ! -f "${LAB_K3D_KUBECONFIG_SINK}" ]]; then
        local temporary_sink="${LAB_K3D_KUBECONFIG_SINK}.tmp"
        printf '%s\n' \
            'apiVersion: v1' \
            'kind: Config' \
            'preferences: {}' \
            'clusters: []' \
            'contexts: []' \
            'current-context: ""' \
            'users: []' >"${temporary_sink}"
        chmod 0600 -- "${temporary_sink}"
        mv -- "${temporary_sink}" "${LAB_K3D_KUBECONFIG_SINK}"
    fi
}

ensure_harbor_registry_config() {
    [[ "${PROFILE}" == full ]] || return 0
    local registry_authority
    registry_authority="$(harbor_registry_authority)"
    local temporary_config="${LAB_REGISTRIES_CONFIG}.tmp"
    printf '%s\n' \
        'mirrors:' \
        "  \"${registry_authority}\":" \
        '    endpoint:' \
        "      - \"http://${registry_authority}\"" \
        'configs:' \
        "  \"${registry_authority}\":" \
        '    tls:' \
        '      insecure_skip_verify: true' >"${temporary_config}"
    chmod 0600 -- "${temporary_config}"
    mv -- "${temporary_config}" "${LAB_REGISTRIES_CONFIG}"
}

harbor_registry_authority() {
    printf 'harbor.pole.localhost:%s' "${HARBOR_PORT}"
}

harbor_registry_ip() {
    run_container inspect "k3d-${PROFILE_CLUSTER_NAMES[0]}-server-0" \
        --format '{{(index .NetworkSettings.Networks "pole-lab").IPAddress}}'
}

configure_harbor_registry_resolution() {
    [[ "${PROFILE}" == full ]] || return 0
    need getent
    getent ahostsv4 harbor.pole.localhost | awk '$1 == "127.0.0.1" { found = 1 } END { exit !found }' \
        || die "The host must resolve harbor.pole.localhost to 127.0.0.1."

    local registry_host=harbor.pole.localhost
    local default_server_ip
    default_server_ip="$(harbor_registry_ip)"
    [[ -n "${default_server_ip}" ]] || die "Could not resolve the default lab server address."

    local cluster_name
    for cluster_name in "${PROFILE_CLUSTER_NAMES[@]}"; do
        local container_id
        while IFS= read -r container_id; do
            [[ -n "${container_id}" ]] || continue
            local role
            role="$(run_container inspect "${container_id}" --format '{{index .Config.Labels "k3d.role"}}')"
            [[ "${role}" == server || "${role}" == agent ]] || continue
            run_container exec "${container_id}" sh -c \
                'grep -v "[[:space:]]$2$" /etc/hosts >/tmp/pole-lab-hosts || true; cat /tmp/pole-lab-hosts >/etc/hosts; printf "%s %s\n" "$1" "$2" >>/etc/hosts; rm -f /tmp/pole-lab-hosts' \
                sh "${default_server_ip}" "${registry_host}"
        done < <(run_container ps --all --filter "label=k3d.cluster=${cluster_name}" --format '{{.ID}}')

        local kubeconfig_path
        kubeconfig_path="$(kubeconfig_path_for "${cluster_name}")"
        local context
        context="$(expected_context_for "${cluster_name}")"
        local node_hosts
        node_hosts="$("${KUBECTL_BIN}" --kubeconfig "${kubeconfig_path}" --context "${context}" \
            --namespace kube-system get configmap coredns --output jsonpath='{.data.NodeHosts}')"
        node_hosts="$(awk -v host="${registry_host}" '$2 != host' <<<"${node_hosts}")"
        node_hosts="${node_hosts}"$'\n'"${default_server_ip} ${registry_host}"$'\n'
        local patch
        patch="$(jq --null-input --compact-output --arg nodeHosts "${node_hosts}" \
            '{data: {NodeHosts: $nodeHosts}}')"
        "${KUBECTL_BIN}" --kubeconfig "${kubeconfig_path}" --context "${context}" \
            --namespace kube-system patch configmap coredns --type merge --patch "${patch}" >/dev/null
    done
}

run_k3d() {
    KUBECONFIG="${LAB_K3D_KUBECONFIG_SINK}" "${K3D_BIN}" "$@"
}

prepare_runtime() {
    umask 077
    validate_state_root
    need jq
    select_container_runtime
    export HELM_CACHE_HOME="${LAB_STATE_ROOT}/helm/cache"
    export HELM_CONFIG_HOME="${LAB_STATE_ROOT}/helm/config"
    export HELM_DATA_HOME="${LAB_STATE_ROOT}/helm/data"
    ensure_tooling
    ensure_k3d_kubeconfig_sink
}

cluster_exists() {
    local cluster_name="$1"
    run_k3d cluster list --no-headers 2>/dev/null \
        | awk -v expected="${cluster_name}" '$1 == expected { found = 1 } END { exit !found }'
}

validate_cluster_container_ownership() {
    local cluster_name="$1"
    local container_ids
    container_ids="$(run_container ps --all --filter "label=k3d.cluster=${cluster_name}" --format '{{.ID}}')"
    [[ -n "${container_ids}" ]] \
        || die "k3d reports cluster '${cluster_name}', but no matching runtime containers were found."
    local container_id
    while IFS= read -r container_id; do
        [[ -n "${container_id}" ]] || continue
        local actual_cluster
        local actual_owner
        local actual_instance_id
        actual_cluster="$(run_container inspect "${container_id}" --format '{{index .Config.Labels "k3d.cluster"}}')"
        actual_owner="$(run_container inspect "${container_id}" --format '{{index .Config.Labels "pole.dev/owned-by"}}')"
        actual_instance_id="$(run_container inspect "${container_id}" --format '{{index .Config.Labels "pole.dev/lab-instance-id"}}')"
        [[ "${actual_cluster}" == "${cluster_name}" ]] \
            || die "Container '${container_id}' does not belong to expected cluster '${cluster_name}'."
        [[ "${actual_owner}" == kubernetes-lab && "${actual_instance_id}" == "${LAB_INSTANCE_ID}" ]] \
            || die "Container '${container_id}' does not belong to Pole lab instance '${LAB_INSTANCE_ID}'."
    done <<<"${container_ids}"
}

registry_exists() {
    run_k3d registry list --no-headers 2>/dev/null \
        | awk -v first="${LAB_REGISTRY_NAME}" -v second="${LAB_REGISTRY_CONTAINER}" \
            '$1 == first || $1 == second { found = 1 } END { exit !found }'
}

validate_registry_container_ownership() {
    [[ -n "${LAB_REGISTRY_CONTAINER_ID}" ]] \
        || die "Lab state does not record the immutable registry container ID; refusing to adopt '${LAB_REGISTRY_CONTAINER}'."
    local actual_container_id
    local registry_role
    actual_container_id="$(run_container inspect "${LAB_REGISTRY_CONTAINER}" --format '{{.Id}}')"
    registry_role="$(run_container inspect "${LAB_REGISTRY_CONTAINER}" --format '{{index .Config.Labels "k3d.role"}}')"
    [[ "${actual_container_id}" == "${LAB_REGISTRY_CONTAINER_ID}" && "${registry_role}" == registry ]] \
        || die "Registry container '${LAB_REGISTRY_CONTAINER}' is not the exact registry recorded by this Pole lab instance."
}

kubeconfig_path_for() {
    printf '%s/%s.yaml' "${LAB_KUBECONFIG_DIR}" "$1"
}

expected_context_for() {
    printf 'k3d-%s' "$1"
}

write_state() {
    local phase="$1"
    [[ "${LAB_INSTANCE_ID}" =~ ^[a-f0-9]{32}$ ]] \
        || die "Cannot write lab state without a valid instance ID."
    if [[ "${phase}" == ready ]]; then
        [[ "${LAB_REGISTRY_CONTAINER_ID}" =~ ^[a-f0-9]{64}$ ]] \
            || die "Cannot mark the lab ready without the immutable registry container ID."
    fi
    local overlays_json
    overlays_json="$(jq --null-input --compact-output '$ARGS.positional' --args "${PROFILE_OVERLAYS[@]}")"
    local targets_json='[]'
    local index
    for ((index = 0; index < ${#PROFILE_CLUSTER_NAMES[@]}; index++)); do
        local cluster_name="${PROFILE_CLUSTER_NAMES[index]}"
        local capabilities='["api", "dns", "ingress", "storage", "registry"]'
        if [[ "${PROFILE}" == topology || ( "${PROFILE}" == full && "${index}" -eq 0 ) ]]; then
            capabilities='["api", "dns", "ingress", "storage", "registry", "topology"]'
        fi
        if [[ "${PROFILE}" == multi || "${PROFILE}" == full ]]; then
            capabilities="$(jq --compact-output '. + ["multi-cluster"] | unique' <<<"${capabilities}")"
        fi
        local target_overlays='[]'
        local overlay
        for overlay in "${PROFILE_OVERLAYS[@]}"; do
            if target_has_overlay "${index}" "${overlay}"; then
                capabilities="$(jq --compact-output --arg capability "${overlay}" '. + [$capability] | unique' <<<"${capabilities}")"
                target_overlays="$(jq --compact-output --arg overlay "${overlay}" '. + [$overlay]' <<<"${target_overlays}")"
            fi
        done
        targets_json="$(jq --compact-output \
            --arg key "${PROFILE_TARGET_KEYS[index]}" \
            --arg clusterName "${cluster_name}" \
            --arg kubeconfigPath "$(kubeconfig_path_for "${cluster_name}")" \
            --arg context "$(expected_context_for "${cluster_name}")" \
            --argjson apiPort "${PROFILE_API_PORTS[index]}" \
            --argjson httpPort "${PROFILE_HTTP_PORTS[index]}" \
            --argjson httpsPort "${PROFILE_HTTPS_PORTS[index]}" \
            --arg publicBaseDomain "${PROFILE_TARGET_KEYS[index]}.apps.localhost" \
            --argjson capabilities "${capabilities}" \
            --argjson overlays "${target_overlays}" \
            '. + [{
                key: $key,
                clusterName: $clusterName,
                kubeconfigPath: $kubeconfigPath,
                context: $context,
                apiPort: $apiPort,
                storageClassName: "local-path",
                publicBaseDomain: $publicBaseDomain,
                overlays: $overlays,
                capabilities: $capabilities,
                ingress: {
                    httpBaseUri: ("http://127.0.0.1:" + ($httpPort | tostring)),
                    httpsBaseUri: ("https://127.0.0.1:" + ($httpsPort | tostring))
                }
            }]' <<<"${targets_json}")"
    done

    mkdir -p -- "${LAB_STATE_ROOT}" "${LAB_KUBECONFIG_DIR}"
    local temporary_state="${LAB_STATE_FILE}.tmp"
    local services_json='{}'
    if [[ "${PROFILE}" == full ]]; then
        local harbor_registry_authority
        harbor_registry_authority="$(harbor_registry_authority)"
        services_json="$(jq --null-input --compact-output \
            --arg credentialsPath "${LAB_STATE_ROOT}/generated/harbor/default-credentials.json" \
            --argjson harborPort "${HARBOR_PORT}" \
            --arg registryHost "${harbor_registry_authority}" \
            '{
                harbor: {
                    targetKey: "default",
                    namespace: "pole-lab-harbor",
                    service: "harbor",
                    port: 80,
                    hostBaseUri: ("http://127.0.0.1:" + ($harborPort | tostring)),
                    registryHost: $registryHost,
                    credentialsPath: $credentialsPath
                },
                buildkit: {
                    targetKey: "default",
                    namespace: "pole-lab-build",
                    service: "buildkit",
                    port: 1234
                }
            }')"
    fi
    jq --null-input \
        --argjson schemaVersion "${LAB_STATE_SCHEMA_VERSION}" \
        --arg instanceId "${LAB_INSTANCE_ID}" \
        --arg profile "${PROFILE}" \
        --arg phase "${phase}" \
        --arg runtimeName "${CONTAINER_RUNTIME}" \
        --argjson runtimeRootless "${CONTAINER_RUNTIME_ROOTLESS}" \
        --arg runtimeDockerHost "${DOCKER_HOST:-}" \
        --arg runtimeDockerSocketPath "${DOCKER_SOCK:-}" \
        --argjson overlays "${overlays_json}" \
        --arg k3dVersion "${K3D_VERSION}" \
        --arg k3sImage "${K3S_IMAGE}" \
        --arg kubectlPath "${KUBECTL_BIN}" \
        --arg helmPath "${HELM_BIN}" \
        --arg buildctlPath "${BUILDCTL_BIN}" \
        --arg registryName "${LAB_REGISTRY_NAME}" \
        --arg registryContainerId "${LAB_REGISTRY_CONTAINER_ID}" \
        --arg registryImage "${REGISTRY_IMAGE}" \
        --argjson registryPort "${REGISTRY_PORT}" \
        --arg registryHost "127.0.0.1:${REGISTRY_PORT}" \
        --arg managedBy pole-kubernetes-lab \
        --arg registryClusterHost "${LAB_REGISTRY_CONTAINER}:${REGISTRY_PORT}" \
        --argjson targets "${targets_json}" \
        --argjson services "${services_json}" \
        '{
            schemaVersion: $schemaVersion,
            managedBy: $managedBy,
            instanceId: $instanceId,
            profile: $profile,
            phase: $phase,
            runtime: {
                name: $runtimeName,
                rootless: $runtimeRootless,
                dockerHost: (if $runtimeDockerHost == "" then null else $runtimeDockerHost end),
                dockerSocketPath: (if $runtimeDockerSocketPath == "" then null else $runtimeDockerSocketPath end),
                testcontainersRyukDisabled: ($runtimeName == "podman")
            },
            overlays: $overlays,
            versions: { k3d: $k3dVersion, k3sImage: $k3sImage },
            tools: { kubectlPath: $kubectlPath, helmPath: $helmPath, buildctlPath: $buildctlPath },
            registry: {
                name: $registryName,
                containerId: (if $registryContainerId == "" then null else $registryContainerId end),
                image: $registryImage,
                port: $registryPort,
                host: $registryHost,
                clusterHost: $registryClusterHost
            },
            targets: $targets,
            services: $services
        }' >"${temporary_state}"
    chmod 0600 -- "${temporary_state}"
    mv -- "${temporary_state}" "${LAB_STATE_FILE}"
}

read_state_profile() {
    [[ -f "${LAB_STATE_FILE}" ]] || return 1
    jq --raw-output '.profile // empty' "${LAB_STATE_FILE}"
}

validate_state_contract() {
    [[ -f "${LAB_STATE_FILE}" ]] || die "No lab state exists at '${LAB_STATE_FILE}'. Run 'lab.sh up' first."
    local schema_version
    schema_version="$(jq --raw-output '.schemaVersion // empty' "${LAB_STATE_FILE}")"
    [[ "${schema_version}" == "${LAB_STATE_SCHEMA_VERSION}" ]] \
        || die "Unsupported or missing lab state schema version '${schema_version:-unknown}'."
    [[ "$(jq --raw-output '.managedBy // empty' "${LAB_STATE_FILE}")" == pole-kubernetes-lab ]] \
        || die "Lab state is missing the exact Pole ownership marker."
    LAB_INSTANCE_ID="$(jq --raw-output '.instanceId // empty' "${LAB_STATE_FILE}")"
    [[ "${LAB_INSTANCE_ID}" =~ ^[a-f0-9]{32}$ ]] \
        || die "Lab state contains an invalid or missing instance ID."
    LAB_REGISTRY_CONTAINER_ID="$(jq --raw-output '.registry.containerId // empty' "${LAB_STATE_FILE}")"
    if [[ -n "${LAB_REGISTRY_CONTAINER_ID}" ]]; then
        [[ "${LAB_REGISTRY_CONTAINER_ID}" =~ ^[a-f0-9]{64}$ ]] \
            || die "Lab state contains an invalid registry container ID."
    elif [[ "$(jq --raw-output '.phase // empty' "${LAB_STATE_FILE}")" == ready ]]; then
        die "Ready lab state is missing the immutable registry container ID."
    fi
    local state_runtime
    state_runtime="$(jq --raw-output '.runtime.name // empty' "${LAB_STATE_FILE}")"
    if [[ -n "${state_runtime}" && "${state_runtime}" != "${CONTAINER_RUNTIME}" ]]; then
        die "Lab state belongs to '${state_runtime}', but the active container runtime is '${CONTAINER_RUNTIME}'."
    fi
    [[ "$(jq --raw-output '.runtime.rootless | type' "${LAB_STATE_FILE}")" == boolean ]] \
        || die "Lab state runtime rootless marker must be boolean."
    [[ "$(jq --raw-output '.runtime.testcontainersRyukDisabled | type' "${LAB_STATE_FILE}")" == boolean ]] \
        || die "Lab state Testcontainers cleanup marker must be boolean."
    if [[ "${state_runtime}" == podman ]]; then
        [[ "$(jq --raw-output '.runtime.dockerHost // empty' "${LAB_STATE_FILE}")" == "${DOCKER_HOST}" ]] \
            || die "Lab state contains a stale Podman Docker API endpoint."
        [[ "$(jq --raw-output '.runtime.dockerSocketPath // empty' "${LAB_STATE_FILE}")" == "${DOCKER_SOCK}" ]] \
            || die "Lab state contains a stale Podman socket path."
        [[ "$(jq --raw-output '.runtime.testcontainersRyukDisabled' "${LAB_STATE_FILE}")" == true ]] \
            || die "Lab state must disable Testcontainers Ryuk for Podman."
    else
        [[ "$(jq --raw-output '.runtime.testcontainersRyukDisabled' "${LAB_STATE_FILE}")" == false ]] \
            || die "Lab state must retain Testcontainers Ryuk for Docker."
    fi

    local state_profile
    state_profile="$(read_state_profile)"
    load_profile "${state_profile}"
    jq --exit-status '.overlays | type == "array"' "${LAB_STATE_FILE}" >/dev/null \
        || die "Lab state overlays must be an array."
    mapfile -t PROFILE_OVERLAYS < <(jq --raw-output '.overlays[]?' "${LAB_STATE_FILE}")
    local overlay
    local -A seen_overlays=()
    for overlay in "${PROFILE_OVERLAYS[@]}"; do
        case "${overlay}" in
            monitoring|cert-manager|cnpg|harbor|buildkit) ;;
            *) die "Lab state contains unknown overlay '${overlay}'." ;;
        esac
        [[ -z "${seen_overlays[${overlay}]:-}" ]] \
            || die "Lab state contains duplicate overlay '${overlay}'."
        seen_overlays["${overlay}"]=true
    done
    if [[ "${PROFILE}" != full ]]; then
        [[ -z "${seen_overlays[harbor]:-}" && -z "${seen_overlays[buildkit]:-}" ]] \
            || die "Focused lab state cannot claim the full profile's Harbor or BuildKit overlays."
    fi
    if [[ "${PROFILE}" == full ]]; then
        local actual_full_overlays
        actual_full_overlays="$(jq --compact-output '.overlays | sort' "${LAB_STATE_FILE}")"
        [[ "${actual_full_overlays}" == '["buildkit","cert-manager","cnpg","harbor","monitoring"]' ]] \
            || die "The full lab state does not contain the exact checked-in overlay contract."
    fi

    [[ "$(jq --raw-output '.registry.name' "${LAB_STATE_FILE}")" == "${LAB_REGISTRY_NAME}" ]] \
        || die "Lab state contains an unexpected registry name."
    [[ "$(jq '.targets | length' "${LAB_STATE_FILE}")" == "${#PROFILE_CLUSTER_NAMES[@]}" ]] \
        || die "Lab state contains an unexpected target count for profile '${PROFILE}'."

    local index
    for ((index = 0; index < ${#PROFILE_CLUSTER_NAMES[@]}; index++)); do
        local cluster_name="${PROFILE_CLUSTER_NAMES[index]}"
        local expected_path
        expected_path="$(kubeconfig_path_for "${cluster_name}")"
        [[ "$(jq --raw-output ".targets[${index}].key" "${LAB_STATE_FILE}")" == "${PROFILE_TARGET_KEYS[index]}" ]] \
            || die "Lab state target key ${index} is not owned by profile '${PROFILE}'."
        [[ "$(jq --raw-output ".targets[${index}].clusterName" "${LAB_STATE_FILE}")" == "${cluster_name}" ]] \
            || die "Lab state cluster ${index} is not owned by profile '${PROFILE}'."
        [[ "$(jq --raw-output ".targets[${index}].kubeconfigPath" "${LAB_STATE_FILE}")" == "${expected_path}" ]] \
            || die "Lab state kubeconfig path ${index} is outside the exact generated contract."
        [[ ! -L "${expected_path}" ]] \
            || die "Lab state kubeconfig path ${index} must not be a symbolic link."
        [[ "$(jq --raw-output ".targets[${index}].context" "${LAB_STATE_FILE}")" == "$(expected_context_for "${cluster_name}")" ]] \
            || die "Lab state context ${index} is outside the exact generated contract."
    done
}

assert_fresh_names_available() {
    local cluster_name
    for cluster_name in "${PROFILE_CLUSTER_NAMES[@]}"; do
        if cluster_exists "${cluster_name}"; then
            die "Cluster '${cluster_name}' already exists without matching Pole lab state; refusing to adopt it."
        fi
    done
    if registry_exists; then
        die "Registry '${LAB_REGISTRY_NAME}' already exists without matching Pole lab state; refusing to adopt it."
    fi
    if network_exists; then
        die "Container network '${LAB_NETWORK_NAME}' already exists without matching Pole lab state; refusing to adopt it."
    fi

    need ss
    local port
    for port in "${REGISTRY_PORT}" "${PROFILE_API_PORTS[@]}" "${PROFILE_HTTP_PORTS[@]}" "${PROFILE_HTTPS_PORTS[@]}"; do
        if ss --no-header --listening --numeric --tcp | awk '{ print $4 }' | grep --extended-regexp --quiet "(^|:)${port}$"; then
            die "Required host TCP port '${port}' is already in use."
        fi
    done
    if [[ "${PROFILE}" == full ]] && ss --no-header --listening --numeric --tcp \
        | awk '{ print $4 }' | grep --extended-regexp --quiet "(^|:)${HARBOR_PORT}$"; then
        die "Required host TCP port '${HARBOR_PORT}' is already in use."
    fi
}

ensure_registry() {
    if registry_exists; then
        validate_registry_container_ownership
        return
    fi
    [[ -z "${LAB_REGISTRY_CONTAINER_ID}" ]] \
        || die "The registry recorded by this lab instance is missing; refusing to replace it implicitly. Run 'lab.sh reset ${PROFILE}'."
    info "Creating shared registry '${LAB_REGISTRY_NAME}' on 127.0.0.1:${REGISTRY_PORT}"
    run_k3d registry create "${LAB_REGISTRY_NAME}" \
        --image "${REGISTRY_IMAGE}" \
        --default-network "${LAB_NETWORK_NAME}" \
        --enforce-port-match \
        --port "127.0.0.1:${REGISTRY_PORT}"
    LAB_REGISTRY_CONTAINER_ID="$(run_container inspect "${LAB_REGISTRY_CONTAINER}" --format '{{.Id}}')"
    [[ "${LAB_REGISTRY_CONTAINER_ID}" =~ ^[a-f0-9]{64}$ ]] \
        || die "The container runtime returned an invalid registry container ID."
    write_state creating
}

network_exists() {
    run_container network inspect "${LAB_NETWORK_NAME}" >/dev/null 2>&1
}

ensure_network() {
    if network_exists; then
        local owner
        local instance_id
        owner="$(run_container network inspect "${LAB_NETWORK_NAME}" --format '{{index .Labels "pole.dev/owned-by"}}')"
        instance_id="$(run_container network inspect "${LAB_NETWORK_NAME}" --format '{{index .Labels "pole.dev/lab-instance-id"}}')"
        [[ "${owner}" == kubernetes-lab && "${instance_id}" == "${LAB_INSTANCE_ID}" ]] \
            || die "Container network '${LAB_NETWORK_NAME}' is not owned by Pole lab instance '${LAB_INSTANCE_ID}'."
        return
    fi
    info "Creating owned container network '${LAB_NETWORK_NAME}'"
    run_container network create \
        --label pole.dev/owned-by=kubernetes-lab \
        --label "${LAB_INSTANCE_LABEL}=${LAB_INSTANCE_ID}" \
        "${LAB_NETWORK_NAME}" >/dev/null
}

ensure_cluster() {
    local index="$1"
    local cluster_name="${PROFILE_CLUSTER_NAMES[index]}"
    if cluster_exists "${cluster_name}"; then
        validate_cluster_container_ownership "${cluster_name}"
    else
        info "Creating cluster '${cluster_name}'"
        local -a create_arguments=(
            cluster create "${cluster_name}"
            --image "${K3S_IMAGE}" \
            --servers 1 \
            --agents "${PROFILE_AGENT_COUNTS[index]}" \
            --api-port "127.0.0.1:${PROFILE_API_PORTS[index]}" \
            --port "127.0.0.1:${PROFILE_HTTP_PORTS[index]}:80@loadbalancer" \
            --port "127.0.0.1:${PROFILE_HTTPS_PORTS[index]}:443@loadbalancer" \
            --network "${LAB_NETWORK_NAME}" \
            --registry-use "${LAB_REGISTRY_CONTAINER}:${REGISTRY_PORT}" \
            --runtime-label 'pole.dev/owned-by=kubernetes-lab@server:*;agent:*;loadbalancer:*' \
            --runtime-label "${LAB_INSTANCE_LABEL}=${LAB_INSTANCE_ID}@server:*;agent:*;loadbalancer:*" \
            --kubeconfig-update-default=false \
            --kubeconfig-switch-context=false \
            --wait
        )
        if [[ "${CONTAINER_RUNTIME}" == podman && "${CONTAINER_RUNTIME_ROOTLESS}" == true ]]; then
            create_arguments+=(
                --k3s-arg '--kubelet-arg=feature-gates=KubeletInUserNamespace=true@server:*;agent:*'
                --k3s-arg '--kube-proxy-arg=conntrack-max-per-core=0@server:*;agent:*'
            )
        fi
        if [[ "${PROFILE}" == full ]]; then
            create_arguments+=(--registry-config "${LAB_REGISTRIES_CONFIG}")
            if [[ "${index}" -eq 0 ]]; then
                create_arguments+=(
                    --port "127.0.0.1:${HARBOR_PORT}:${HARBOR_PORT}@server:0"
                    --k3s-arg '--kube-apiserver-arg=service-node-port-range=5000-32767@server:*'
                )
            fi
        fi
        run_k3d "${create_arguments[@]}"
    fi

    local kubeconfig_path
    kubeconfig_path="$(kubeconfig_path_for "${cluster_name}")"
    local temporary_kubeconfig="${kubeconfig_path}.tmp"
    run_k3d kubeconfig get "${cluster_name}" >"${temporary_kubeconfig}"
    chmod 0600 -- "${temporary_kubeconfig}"
    "${KUBECTL_BIN}" --kubeconfig "${temporary_kubeconfig}" config set-cluster \
        "$(expected_context_for "${cluster_name}")" \
        --server="https://127.0.0.1:${PROFILE_API_PORTS[index]}" >/dev/null
    mv -- "${temporary_kubeconfig}" "${kubeconfig_path}"

    local actual_contexts
    actual_contexts="$(${KUBECTL_BIN} --kubeconfig "${kubeconfig_path}" config get-contexts --output name)"
    [[ "${actual_contexts}" == "$(expected_context_for "${cluster_name}")" ]] \
        || die "Cluster '${cluster_name}' exported unexpected context(s): ${actual_contexts//$'\n'/, }."
}

configure_topology() {
    [[ "${PROFILE}" == topology || "${PROFILE}" == full ]] || return 0
    local cluster_name="${PROFILE_CLUSTER_NAMES[0]}"
    local kubeconfig_path
    kubeconfig_path="$(kubeconfig_path_for "${cluster_name}")"
    local context
    context="$(expected_context_for "${cluster_name}")"

    info "Applying deterministic topology labels and taints"
    "${KUBECTL_BIN}" --kubeconfig "${kubeconfig_path}" --context "${context}" \
        label node "k3d-${cluster_name}-agent-0" pole.dev/compute-pool=general --overwrite
    "${KUBECTL_BIN}" --kubeconfig "${kubeconfig_path}" --context "${context}" \
        label node "k3d-${cluster_name}-agent-1" pole.dev/compute-pool=memory --overwrite
    "${KUBECTL_BIN}" --kubeconfig "${kubeconfig_path}" --context "${context}" \
        label node "k3d-${cluster_name}-agent-2" pole.dev/compute-pool=isolated --overwrite
    "${KUBECTL_BIN}" --kubeconfig "${kubeconfig_path}" --context "${context}" \
        taint node "k3d-${cluster_name}-agent-2" pole.dev/dedicated=isolated:NoSchedule --overwrite
}

run_helm_for_target() {
    local index="$1"
    shift
    "${HELM_BIN}" "$@" \
        --kubeconfig "$(kubeconfig_path_for "${PROFILE_CLUSTER_NAMES[index]}")" \
        --kube-context "$(expected_context_for "${PROFILE_CLUSTER_NAMES[index]}")"
}

install_monitoring_overlay() {
    local index="$1"
    run_helm_for_target "${index}" upgrade --install pole-lab-monitoring \
        oci://ghcr.io/prometheus-community/charts/kube-prometheus-stack \
        --version "${PROMETHEUS_STACK_CHART_VERSION}" \
        --namespace pole-lab-monitoring --create-namespace --wait --timeout 5m \
        --set defaultRules.create=false \
        --set alertmanager.enabled=false \
        --set grafana.enabled=false \
        --set kubeApiServer.enabled=false \
        --set kubelet.enabled=false \
        --set kubeControllerManager.enabled=false \
        --set coreDns.enabled=false \
        --set kubeDns.enabled=false \
        --set kubeEtcd.enabled=false \
        --set kubeScheduler.enabled=false \
        --set kubeProxy.enabled=false \
        --set kubeStateMetrics.enabled=false \
        --set nodeExporter.enabled=false \
        --set prometheus.enabled=false \
        --set prometheusOperator.enabled=false
}

install_cert_manager_overlay() {
    local index="$1"
    run_helm_for_target "${index}" upgrade --install pole-lab-cert-manager \
        oci://quay.io/jetstack/charts/cert-manager \
        --version "${CERT_MANAGER_CHART_VERSION}" \
        --namespace pole-lab-cert-manager --create-namespace --wait --timeout 5m \
        --set crds.enabled=true
    "${KUBECTL_BIN}" \
        --kubeconfig "$(kubeconfig_path_for "${PROFILE_CLUSTER_NAMES[index]}")" \
        --context "$(expected_context_for "${PROFILE_CLUSTER_NAMES[index]}")" \
        apply --filename "${LAB_SCRIPT_DIR}/cert-manager-issuer.yaml" >/dev/null
    "${KUBECTL_BIN}" \
        --kubeconfig "$(kubeconfig_path_for "${PROFILE_CLUSTER_NAMES[index]}")" \
        --context "$(expected_context_for "${PROFILE_CLUSTER_NAMES[index]}")" \
        wait --for=condition=Ready clusterissuer/pole-lab --timeout=90s >/dev/null
}

install_cnpg_overlay() {
    local index="$1"
    run_helm_for_target "${index}" upgrade --install pole-lab-cnpg \
        oci://ghcr.io/cloudnative-pg/charts/cloudnative-pg \
        --version "${CNPG_CHART_VERSION}" \
        --namespace pole-lab-cnpg --create-namespace --wait --timeout 5m
}

install_harbor_overlay() {
    local index="$1"
    need openssl
    local target_key="${PROFILE_TARGET_KEYS[index]}"
    local values_dir="${LAB_STATE_ROOT}/generated/harbor"
    local values_path="${values_dir}/${target_key}.yaml"
    local credentials_path="${values_dir}/${target_key}-credentials.json"
    mkdir -p -- "${values_dir}"
    local admin_password
    if [[ -f "${credentials_path}" ]]; then
        admin_password="$(jq --raw-output '.password // empty' "${credentials_path}")"
        [[ -n "${admin_password}" ]] || die "Harbor credentials file '${credentials_path}' is malformed."
    elif [[ -f "${values_path}" ]]; then
        admin_password="$(awk '/^harborAdminPassword:/ { sub(/^[^:]+:[[:space:]]*/, ""); print; exit }' "${values_path}")"
        [[ -n "${admin_password}" ]] || die "Existing Harbor values do not contain an admin password."
        jq --null-input --arg username admin --arg password "${admin_password}" \
            '{username: $username, password: $password}' >"${credentials_path}.tmp"
        chmod 0600 -- "${credentials_path}.tmp"
        mv -- "${credentials_path}.tmp" "${credentials_path}"
    else
        admin_password="$(openssl rand -hex 18)"
        jq --null-input --arg username admin --arg password "${admin_password}" \
            '{username: $username, password: $password}' >"${credentials_path}.tmp"
        chmod 0600 -- "${credentials_path}.tmp"
        mv -- "${credentials_path}.tmp" "${credentials_path}"
    fi

    if [[ ! -f "${values_path}" ]]; then
        local secret_key
        local registry_authority
        registry_authority="$(harbor_registry_authority)"
        secret_key="$(openssl rand -hex 8)"
        printf '%s\n' \
            'expose:' \
            '  type: nodePort' \
            '  tls:' \
            '    enabled: false' \
            '  nodePort:' \
            '    ports:' \
            '      http:' \
            "        nodePort: ${HARBOR_PORT}" \
            "externalURL: http://${registry_authority}" \
            'persistence:' \
            '  enabled: false' \
            'trivy:' \
            '  enabled: false' \
            "harborAdminPassword: ${admin_password}" \
            "secretKey: ${secret_key}" >"${values_path}"
        chmod 0600 -- "${values_path}"
    fi
    run_helm_for_target "${index}" upgrade --install pole-lab-harbor \
        "https://helm.goharbor.io/harbor-${HARBOR_CHART_VERSION}.tgz" \
        --namespace pole-lab-harbor --create-namespace --wait --timeout 10m \
        --values "${values_path}"
}

install_buildkit_overlay() {
    local index="$1"
    grep --fixed-strings --quiet "image: ${BUILDKIT_IMAGE}" "${LAB_SCRIPT_DIR}/buildkit.yaml" \
        || die "The BuildKit manifest does not match pinned image '${BUILDKIT_IMAGE}'."
    grep --fixed-strings --quiet 'POLE_LAB_HARBOR_REGISTRY' "${LAB_SCRIPT_DIR}/buildkit.yaml" \
        || die "The BuildKit manifest does not trust the lab Harbor registry authority."
    grep --fixed-strings --quiet 'POLE_LAB_HARBOR_IP' "${LAB_SCRIPT_DIR}/buildkit.yaml" \
        || die "The BuildKit manifest does not contain the lab Harbor host alias contract."
    local generated_manifest="${LAB_STATE_ROOT}/generated/buildkit.yaml"
    mkdir -p -- "$(dirname -- "${generated_manifest}")"
    sed \
        -e "s|POLE_LAB_HARBOR_REGISTRY|$(harbor_registry_authority)|g" \
        -e "s|POLE_LAB_HARBOR_IP|$(harbor_registry_ip)|g" \
        "${LAB_SCRIPT_DIR}/buildkit.yaml" >"${generated_manifest}.tmp"
    chmod 0600 -- "${generated_manifest}.tmp"
    mv -- "${generated_manifest}.tmp" "${generated_manifest}"
    "${KUBECTL_BIN}" \
        --kubeconfig "$(kubeconfig_path_for "${PROFILE_CLUSTER_NAMES[index]}")" \
        --context "$(expected_context_for "${PROFILE_CLUSTER_NAMES[index]}")" \
        apply --filename "${generated_manifest}" >/dev/null
    "${KUBECTL_BIN}" \
        --kubeconfig "$(kubeconfig_path_for "${PROFILE_CLUSTER_NAMES[index]}")" \
        --context "$(expected_context_for "${PROFILE_CLUSTER_NAMES[index]}")" \
        --namespace pole-lab-build rollout status deployment/buildkit --timeout=5m >/dev/null
}

install_overlays() {
    [[ "${#PROFILE_OVERLAYS[@]}" -gt 0 ]] || return 0
    local index
    for ((index = 0; index < ${#PROFILE_CLUSTER_NAMES[@]}; index++)); do
        local overlay
        for overlay in "${PROFILE_OVERLAYS[@]}"; do
            target_has_overlay "${index}" "${overlay}" || continue
            info "Installing '${overlay}' on '${PROFILE_CLUSTER_NAMES[index]}'"
            case "${overlay}" in
                monitoring) install_monitoring_overlay "${index}" ;;
                cert-manager) install_cert_manager_overlay "${index}" ;;
                cnpg) install_cnpg_overlay "${index}" ;;
                harbor) install_harbor_overlay "${index}" ;;
                buildkit) install_buildkit_overlay "${index}" ;;
            esac
        done
    done
}

up_lab() {
    parse_profile_and_overlays "$@"
    prepare_runtime
    ensure_rootless_cgroup_delegation
    ensure_rootless_inotify_capacity

    if [[ -f "${LAB_STATE_FILE}" ]]; then
        local requested_overlays
        requested_overlays="$(jq --null-input --compact-output '$ARGS.positional' --args "${PROFILE_OVERLAYS[@]}")"
        local existing_profile
        existing_profile="$(read_state_profile)"
        [[ "${existing_profile}" == "${PROFILE}" ]] \
            || die "Profile '${existing_profile}' is already active. Run 'lab.sh reset ${PROFILE}' to replace it."
        [[ "$(jq --compact-output '.overlays // []' "${LAB_STATE_FILE}")" == "${requested_overlays}" ]] \
            || die "The active lab uses different overlays. Run 'lab.sh reset ${PROFILE}' with the requested overlays."
        validate_state_contract
        if [[ "$(jq --raw-output '.phase' "${LAB_STATE_FILE}")" != ready ]]; then
            warn "Recovering the exact Pole lab resources left by an interrupted startup."
            down_lab
        fi
    fi
    if [[ ! -f "${LAB_STATE_FILE}" ]]; then
        assert_fresh_names_available
        LAB_INSTANCE_ID="$(generate_lab_instance_id)"
        LAB_REGISTRY_CONTAINER_ID=""
        write_state creating
    fi

    ensure_network
    ensure_registry
    ensure_harbor_registry_config
    local index
    for ((index = 0; index < ${#PROFILE_CLUSTER_NAMES[@]}; index++)); do
        ensure_cluster "${index}"
    done
    configure_harbor_registry_resolution
    configure_topology
    install_overlays
    write_state ready
    info "Profile '${PROFILE}' is ready. State: ${LAB_STATE_FILE}"
}

status_lab() {
    prepare_runtime
    if [[ ! -f "${LAB_STATE_FILE}" ]]; then
        info "No Pole Kubernetes lab is active."
        return
    fi
    validate_state_contract
    printf 'Profile: %s\n' "${PROFILE}"
    printf 'Phase: %s\n' "$(jq --raw-output '.phase' "${LAB_STATE_FILE}")"
    printf 'Runtime: %s\n' "${CONTAINER_RUNTIME}"
    printf 'Overlays: %s\n' "$(jq --raw-output 'if (.overlays | length) == 0 then "none" else (.overlays | join(", ")) end' "${LAB_STATE_FILE}")"
    printf 'Network: %s (%s)\n' "${LAB_NETWORK_NAME}" "$(network_exists && printf running || printf missing)"
    printf 'Registry: %s (%s)\n' "${LAB_REGISTRY_NAME}" "$(registry_exists && printf running || printf missing)"
    local cluster_name
    for cluster_name in "${PROFILE_CLUSTER_NAMES[@]}"; do
        printf 'Cluster: %s (%s)\n' "${cluster_name}" "$(cluster_exists "${cluster_name}" && printf running || printf missing)"
    done
}

verify_registry_pull() {
    local image_tag="pole-lab-${PROFILE}-${VERIFY_RUN_ID}"
    local host_image="127.0.0.1:${REGISTRY_PORT}/pole/lab-smoke:${image_tag}"
    local cluster_image="${LAB_REGISTRY_CONTAINER}:${REGISTRY_PORT}/pole/lab-smoke:${image_tag}"
    info "Pushing one pinned smoke image to the shared registry"
    run_container pull "${PAUSE_IMAGE}" >/dev/null
    run_container tag "${PAUSE_IMAGE}" "${host_image}"
    if [[ "${CONTAINER_RUNTIME}" == podman ]]; then
        run_container push --tls-verify=false "${host_image}" >/dev/null
    else
        run_container push "${host_image}" >/dev/null
    fi

    local index
    for ((index = 0; index < ${#PROFILE_CLUSTER_NAMES[@]}; index++)); do
        local cluster_name="${PROFILE_CLUSTER_NAMES[index]}"
        local kubeconfig_path
        kubeconfig_path="$(kubeconfig_path_for "${cluster_name}")"
        local context
        context="$(expected_context_for "${cluster_name}")"
        local namespace_name="pole-lab-verify-${PROFILE}-${index}-${VERIFY_RUN_ID}"
        local ingress_host="verify-${VERIFY_RUN_ID}-${index}.${PROFILE_TARGET_KEYS[index]}.apps.localhost"

        "${KUBECTL_BIN}" --kubeconfig "${kubeconfig_path}" --context "${context}" create namespace "${namespace_name}" >/dev/null
        VERIFY_TARGET_INDEXES+=("${index}")
        VERIFY_NAMESPACE_NAMES+=("${namespace_name}")
        "${KUBECTL_BIN}" --kubeconfig "${kubeconfig_path}" --context "${context}" \
            label namespace "${namespace_name}" pole.dev/owned-by=kubernetes-lab --overwrite >/dev/null
        "${KUBECTL_BIN}" --kubeconfig "${kubeconfig_path}" --context "${context}" --namespace "${namespace_name}" \
            run registry-pull --image "${cluster_image}" --image-pull-policy Always \
            --labels pole.dev/owned-by=kubernetes-lab --restart Never >/dev/null
        "${KUBECTL_BIN}" --kubeconfig "${kubeconfig_path}" --context "${context}" --namespace "${namespace_name}" \
            wait --for=condition=Ready pod/registry-pull --timeout=90s >/dev/null

        "${KUBECTL_BIN}" --kubeconfig "${kubeconfig_path}" --context "${context}" --namespace "${namespace_name}" \
            run dns-smoke --image "${BUSYBOX_IMAGE}" --restart Never --command -- \
            nslookup kubernetes.default.svc.cluster.local >/dev/null
        "${KUBECTL_BIN}" --kubeconfig "${kubeconfig_path}" --context "${context}" --namespace "${namespace_name}" \
            wait --for=jsonpath='{.status.phase}'=Succeeded pod/dns-smoke --timeout=90s >/dev/null

        "${KUBECTL_BIN}" --kubeconfig "${kubeconfig_path}" --context "${context}" --namespace "${namespace_name}" \
            create serviceaccount rbac-smoke >/dev/null
        "${KUBECTL_BIN}" --kubeconfig "${kubeconfig_path}" --context "${context}" --namespace "${namespace_name}" \
            create role rbac-smoke --verb=get,list --resource=configmaps >/dev/null
        "${KUBECTL_BIN}" --kubeconfig "${kubeconfig_path}" --context "${context}" --namespace "${namespace_name}" \
            create rolebinding rbac-smoke --role=rbac-smoke --serviceaccount="${namespace_name}:rbac-smoke" >/dev/null
        "${KUBECTL_BIN}" --kubeconfig "${kubeconfig_path}" --context "${context}" --namespace "${namespace_name}" \
            auth can-i get configmaps --as="system:serviceaccount:${namespace_name}:rbac-smoke" \
            | grep --fixed-strings --line-regexp --quiet yes \
            || die "RBAC smoke did not grant ConfigMap reads in '${namespace_name}'."
        local secret_delete_permission
        secret_delete_permission="$(
            "${KUBECTL_BIN}" --kubeconfig "${kubeconfig_path}" --context "${context}" --namespace "${namespace_name}" \
                auth can-i delete secrets --as="system:serviceaccount:${namespace_name}:rbac-smoke" 2>/dev/null || true
        )"
        [[ "${secret_delete_permission}" == no ]] \
            || die "RBAC smoke unexpectedly granted Secret deletion in '${namespace_name}'."

        jq --null-input \
            --arg namespace "${namespace_name}" \
            --arg storageClass local-path \
            '{
                apiVersion: "v1",
                kind: "PersistentVolumeClaim",
                metadata: { name: "storage-smoke", namespace: $namespace },
                spec: {
                    accessModes: ["ReadWriteOnce"],
                    storageClassName: $storageClass,
                    resources: { requests: { storage: "32Mi" } }
                }
            }' \
            | "${KUBECTL_BIN}" --kubeconfig "${kubeconfig_path}" --context "${context}" apply --filename - >/dev/null
        jq --null-input \
            --arg namespace "${namespace_name}" \
            --arg image "${BUSYBOX_IMAGE}" \
            '{
                apiVersion: "v1",
                kind: "Pod",
                metadata: {
                    name: "storage-smoke",
                    namespace: $namespace,
                    labels: { "pole.dev/owned-by": "kubernetes-lab" }
                },
                spec: {
                    restartPolicy: "Never",
                    containers: [{
                        name: "storage-smoke",
                        image: $image,
                        command: ["sh", "-c", "printf pole-lab >/data/probe && grep -q pole-lab /data/probe"],
                        volumeMounts: [{ name: "data", mountPath: "/data" }]
                    }],
                    volumes: [{ name: "data", persistentVolumeClaim: { claimName: "storage-smoke" } }]
                }
            }' \
            | "${KUBECTL_BIN}" --kubeconfig "${kubeconfig_path}" --context "${context}" apply --filename - >/dev/null
        "${KUBECTL_BIN}" --kubeconfig "${kubeconfig_path}" --context "${context}" --namespace "${namespace_name}" \
            wait --for=jsonpath='{.status.phase}'=Succeeded pod/storage-smoke --timeout=120s >/dev/null

        "${KUBECTL_BIN}" --kubeconfig "${kubeconfig_path}" --context "${context}" --namespace "${namespace_name}" \
            create deployment ingress-smoke --image "${NGINX_IMAGE}" >/dev/null
        "${KUBECTL_BIN}" --kubeconfig "${kubeconfig_path}" --context "${context}" --namespace "${namespace_name}" \
            expose deployment ingress-smoke --port 80 >/dev/null
        "${KUBECTL_BIN}" --kubeconfig "${kubeconfig_path}" --context "${context}" --namespace "${namespace_name}" \
            create ingress ingress-smoke --class traefik --rule "${ingress_host}/*=ingress-smoke:80" >/dev/null
        "${KUBECTL_BIN}" --kubeconfig "${kubeconfig_path}" --context "${context}" --namespace "${namespace_name}" \
            rollout status deployment/ingress-smoke --timeout=120s >/dev/null
        local ingress_uri="http://127.0.0.1:${PROFILE_HTTP_PORTS[index]}/"
        local attempt
        for attempt in {1..30}; do
            if curl --fail --silent --show-error --header "Host: ${ingress_host}" "${ingress_uri}" >/dev/null; then
                break
            fi
            [[ "${attempt}" -lt 30 ]] || die "Ingress smoke failed for '${cluster_name}' via '${ingress_uri}'."
            sleep 2
        done

        "${KUBECTL_BIN}" --kubeconfig "${kubeconfig_path}" --context "${context}" \
            delete namespace "${namespace_name}" --wait=true --timeout=90s >/dev/null
        info "Verified API, DNS, ingress, storage, RBAC, and shared-registry pull on '${cluster_name}'"
    done

    run_container image rm "${host_image}" >/dev/null 2>&1 || true
}

cleanup_verification_namespaces() {
    set +e
    if [[ -n "${VERIFY_BUILDKIT_FORWARD_PID}" ]]; then
        kill "${VERIFY_BUILDKIT_FORWARD_PID}" >/dev/null 2>&1
        wait "${VERIFY_BUILDKIT_FORWARD_PID}" >/dev/null 2>&1
        VERIFY_BUILDKIT_FORWARD_PID=""
    fi
    if [[ -n "${VERIFY_HARBOR_IMAGE}" ]]; then
        local cluster_name
        for cluster_name in "${PROFILE_CLUSTER_NAMES[@]}"; do
            run_container exec "k3d-${cluster_name}-server-0" crictl rmi "${VERIFY_HARBOR_IMAGE}" >/dev/null 2>&1
        done
    fi
    if [[ -n "${VERIFY_HARBOR_PROJECT}" && -f "${LAB_STATE_ROOT}/generated/harbor/default-credentials.json" ]]; then
        local harbor_username
        local harbor_password
        harbor_username="$(jq --raw-output '.username // empty' "${LAB_STATE_ROOT}/generated/harbor/default-credentials.json")"
        harbor_password="$(jq --raw-output '.password // empty' "${LAB_STATE_ROOT}/generated/harbor/default-credentials.json")"
        curl --silent --show-error --user "${harbor_username}:${harbor_password}" \
            --request DELETE "http://127.0.0.1:${HARBOR_PORT}/api/v2.0/projects/${VERIFY_HARBOR_PROJECT}" \
            >/dev/null 2>&1
    fi
    if [[ -n "${VERIFY_RUN_ID}" ]]; then
        rm -rf -- "${LAB_STATE_ROOT}/generated/verify-${VERIFY_RUN_ID}"
    fi
    local item
    for ((item = 0; item < ${#VERIFY_NAMESPACE_NAMES[@]}; item++)); do
        local index="${VERIFY_TARGET_INDEXES[item]}"
        "${KUBECTL_BIN}" \
            --kubeconfig "$(kubeconfig_path_for "${PROFILE_CLUSTER_NAMES[index]}")" \
            --context "$(expected_context_for "${PROFILE_CLUSTER_NAMES[index]}")" \
            delete namespace "${VERIFY_NAMESPACE_NAMES[item]}" --ignore-not-found --wait=false >/dev/null 2>&1
    done
    if [[ -n "${VERIFY_RUN_ID}" ]]; then
        local index
        for ((index = 0; index < ${#PROFILE_CLUSTER_NAMES[@]}; index++)); do
            target_has_overlay "${index}" buildkit || continue
            "${KUBECTL_BIN}" \
                --kubeconfig "$(kubeconfig_path_for "${PROFILE_CLUSTER_NAMES[index]}")" \
                --context "$(expected_context_for "${PROFILE_CLUSTER_NAMES[index]}")" \
                --namespace pole-lab-build \
                delete "pod/buildkit-connectivity-${VERIFY_RUN_ID}" --ignore-not-found --wait=false >/dev/null 2>&1
        done
    fi
}

verify_harbor_buildkit_path() {
    local credentials_path="${LAB_STATE_ROOT}/generated/harbor/default-credentials.json"
    local harbor_username
    local harbor_password
    harbor_username="$(jq --raw-output '.username // empty' "${credentials_path}")"
    harbor_password="$(jq --raw-output '.password // empty' "${credentials_path}")"
    [[ -n "${harbor_username}" && -n "${harbor_password}" ]] \
        || die "Harbor verification credentials are missing or malformed."

    VERIFY_HARBOR_PROJECT="lab-verify-${VERIFY_RUN_ID}"
    curl --fail --silent --show-error --user "${harbor_username}:${harbor_password}" \
        --header 'Content-Type: application/json' \
        --request POST \
        --data "{\"project_name\":\"${VERIFY_HARBOR_PROJECT}\",\"metadata\":{\"public\":\"true\"}}" \
        "http://127.0.0.1:${HARBOR_PORT}/api/v2.0/projects" >/dev/null

    local forward_port=""
    local candidate
    for candidate in $(seq 52100 52199); do
        if ! ss --no-header --listening --numeric --tcp | awk '{ print $4 }' \
            | grep --extended-regexp --quiet "(^|:)${candidate}$"; then
            forward_port="${candidate}"
            break
        fi
    done
    [[ -n "${forward_port}" ]] || die "Could not reserve a loopback port for BuildKit verification."

    local verify_directory="${LAB_STATE_ROOT}/generated/verify-${VERIFY_RUN_ID}"
    local context_directory="${verify_directory}/context"
    local docker_config_directory="${verify_directory}/docker"
    mkdir -p -- "${context_directory}" "${docker_config_directory}"
    VERIFY_BUILDKIT_FORWARD_LOG="${verify_directory}/port-forward.log"
    "${KUBECTL_BIN}" \
        --kubeconfig "$(kubeconfig_path_for "${PROFILE_CLUSTER_NAMES[0]}")" \
        --context "$(expected_context_for "${PROFILE_CLUSTER_NAMES[0]}")" \
        --namespace pole-lab-build port-forward --address 127.0.0.1 service/buildkit \
        "${forward_port}:1234" >"${VERIFY_BUILDKIT_FORWARD_LOG}" 2>&1 &
    VERIFY_BUILDKIT_FORWARD_PID="$!"
    local attempt
    for attempt in {1..30}; do
        if (printf '' >/dev/tcp/127.0.0.1/"${forward_port}") 2>/dev/null; then
            break
        fi
        kill -0 "${VERIFY_BUILDKIT_FORWARD_PID}" >/dev/null 2>&1 \
            || die "BuildKit verification port-forward exited: $(<"${VERIFY_BUILDKIT_FORWARD_LOG}")"
        [[ "${attempt}" -lt 30 ]] || die "BuildKit verification port-forward did not become ready."
        sleep 1
    done

    printf '%s\n' 'FROM scratch' 'LABEL pole.dev/lab-verification=true' >"${context_directory}/Dockerfile"
    local registry_authority
    registry_authority="$(harbor_registry_authority)"
    local registry_auth
    registry_auth="$(printf '%s' "${harbor_username}:${harbor_password}" | base64 --wrap=0)"
    jq --null-input --arg registry "${registry_authority}" --arg auth "${registry_auth}" \
        '{auths: {($registry): {auth: $auth}}}' >"${docker_config_directory}/config.json"
    chmod 0600 -- "${docker_config_directory}/config.json"
    VERIFY_HARBOR_IMAGE="${registry_authority}/${VERIFY_HARBOR_PROJECT}/scratch:${VERIFY_RUN_ID}"
    info "Building and pushing a scratch image through the lab BuildKit and Harbor path"
    DOCKER_CONFIG="${docker_config_directory}" "${BUILDCTL_BIN}" \
        --addr "tcp://127.0.0.1:${forward_port}" build \
        --frontend dockerfile.v0 \
        --local "context=${context_directory}" \
        --local "dockerfile=${context_directory}" \
        --output "type=image,name=${VERIFY_HARBOR_IMAGE},push=true,registry.insecure=true" >/dev/null

    local cluster_name
    for cluster_name in "${PROFILE_CLUSTER_NAMES[@]}"; do
        run_container exec "k3d-${cluster_name}-server-0" crictl pull "${VERIFY_HARBOR_IMAGE}" >/dev/null
    done
    info "Verified BuildKit push and Harbor pull from every lab cluster"
}

verify_lab() {
    prepare_runtime
    validate_state_contract
    VERIFY_RUN_ID="$(printf '%s:%s:%s:%s' "${PROFILE}" "$$" "${RANDOM}" "$(date +%s%N)" | sha256sum | cut -c1-12)"
    [[ "$(jq --raw-output '.phase' "${LAB_STATE_FILE}")" == ready ]] \
        || die "Lab is not ready; inspect 'lab.sh status' and retry 'lab.sh up ${PROFILE}'."
    registry_exists || die "Expected registry '${LAB_REGISTRY_NAME}' is missing."
    validate_registry_container_ownership

    local index
    for ((index = 0; index < ${#PROFILE_CLUSTER_NAMES[@]}; index++)); do
        local cluster_name="${PROFILE_CLUSTER_NAMES[index]}"
        cluster_exists "${cluster_name}" || die "Expected cluster '${cluster_name}' is missing."
        validate_cluster_container_ownership "${cluster_name}"
        "${KUBECTL_BIN}" \
            --kubeconfig "$(kubeconfig_path_for "${cluster_name}")" \
            --context "$(expected_context_for "${cluster_name}")" \
            get --raw=/readyz >/dev/null
    done
    verify_registry_pull
    local overlay
    for overlay in "${PROFILE_OVERLAYS[@]}"; do
        for ((index = 0; index < ${#PROFILE_CLUSTER_NAMES[@]}; index++)); do
            target_has_overlay "${index}" "${overlay}" || continue
            local kubeconfig_path
            kubeconfig_path="$(kubeconfig_path_for "${PROFILE_CLUSTER_NAMES[index]}")"
            local context
            context="$(expected_context_for "${PROFILE_CLUSTER_NAMES[index]}")"
            case "${overlay}" in
                monitoring)
                    "${KUBECTL_BIN}" --kubeconfig "${kubeconfig_path}" --context "${context}" get customresourcedefinition servicemonitors.monitoring.coreos.com >/dev/null
                    ;;
                cert-manager)
                    "${KUBECTL_BIN}" --kubeconfig "${kubeconfig_path}" --context "${context}" --namespace pole-lab-cert-manager rollout status deployment/pole-lab-cert-manager --timeout=90s >/dev/null
                    "${KUBECTL_BIN}" --kubeconfig "${kubeconfig_path}" --context "${context}" wait --for=condition=Ready clusterissuer/pole-lab --timeout=90s >/dev/null
                    ;;
                cnpg)
                    "${KUBECTL_BIN}" --kubeconfig "${kubeconfig_path}" --context "${context}" --namespace pole-lab-cnpg rollout status deployment/pole-lab-cnpg-cloudnative-pg --timeout=90s >/dev/null
                    ;;
                harbor)
                    "${KUBECTL_BIN}" --kubeconfig "${kubeconfig_path}" --context "${context}" --namespace pole-lab-harbor wait --for=condition=Available deployment --all --timeout=180s >/dev/null
                    local harbor_attempt
                    for harbor_attempt in {1..60}; do
                        if curl --fail --silent --show-error "http://127.0.0.1:${HARBOR_PORT}/api/v2.0/health" \
                            | jq --exit-status '.status == "healthy"' >/dev/null; then
                            break
                        fi
                        [[ "${harbor_attempt}" -lt 60 ]] \
                            || die "Harbor did not report healthy through 127.0.0.1:${HARBOR_PORT}."
                        sleep 2
                    done
                    ;;
                buildkit)
                    "${KUBECTL_BIN}" --kubeconfig "${kubeconfig_path}" --context "${context}" --namespace pole-lab-build rollout status deployment/buildkit --timeout=90s >/dev/null
                    "${KUBECTL_BIN}" --kubeconfig "${kubeconfig_path}" --context "${context}" --namespace pole-lab-build \
                        run "buildkit-connectivity-${VERIFY_RUN_ID}" --image "${BUSYBOX_IMAGE}" --restart Never --command -- \
                        sh -c 'nc -z -w 5 buildkit 1234' >/dev/null
                    "${KUBECTL_BIN}" --kubeconfig "${kubeconfig_path}" --context "${context}" --namespace pole-lab-build \
                        wait --for=jsonpath='{.status.phase}'=Succeeded "pod/buildkit-connectivity-${VERIFY_RUN_ID}" --timeout=90s >/dev/null
                    "${KUBECTL_BIN}" --kubeconfig "${kubeconfig_path}" --context "${context}" --namespace pole-lab-build \
                        delete "pod/buildkit-connectivity-${VERIFY_RUN_ID}" --wait=true --timeout=30s >/dev/null
                    ;;
            esac
        done
    done
    if [[ "${PROFILE}" == full ]]; then
        verify_harbor_buildkit_path
    fi
    info "Profile '${PROFILE}' passed lab verification."
}

delete_cluster_if_present() {
    local cluster_name="$1"
    if cluster_exists "${cluster_name}"; then
        validate_cluster_container_ownership "${cluster_name}"
        info "Deleting owned cluster '${cluster_name}'"
        run_k3d cluster delete "${cluster_name}"
    fi
}

down_lab() {
    prepare_runtime
    if [[ ! -f "${LAB_STATE_FILE}" ]]; then
        info "No Pole Kubernetes lab state exists; nothing to delete."
        return
    fi
    validate_state_contract

    local cluster_name
    for cluster_name in "${PROFILE_CLUSTER_NAMES[@]}"; do
        delete_cluster_if_present "${cluster_name}"
    done
    if [[ -d "${LAB_STATE_ROOT}/generated/harbor" ]]; then
        local target_key
        for target_key in "${PROFILE_TARGET_KEYS[@]}"; do
            rm -f -- \
                "${LAB_STATE_ROOT}/generated/harbor/${target_key}.yaml" \
                "${LAB_STATE_ROOT}/generated/harbor/${target_key}-credentials.json"
        done
        rmdir -- "${LAB_STATE_ROOT}/generated/harbor" 2>/dev/null || true
        rmdir -- "${LAB_STATE_ROOT}/generated" 2>/dev/null || true
    fi
    if registry_exists; then
        validate_registry_container_ownership
        info "Deleting owned registry '${LAB_REGISTRY_NAME}'"
        run_k3d registry delete "${LAB_REGISTRY_NAME}"
    fi
    if network_exists; then
        local owner
        local instance_id
        owner="$(run_container network inspect "${LAB_NETWORK_NAME}" --format '{{index .Labels "pole.dev/owned-by"}}')"
        instance_id="$(run_container network inspect "${LAB_NETWORK_NAME}" --format '{{index .Labels "pole.dev/lab-instance-id"}}')"
        [[ "${owner}" == kubernetes-lab && "${instance_id}" == "${LAB_INSTANCE_ID}" ]] \
            || die "Refusing to delete container network '${LAB_NETWORK_NAME}' without the exact Pole lab instance labels."
        info "Deleting owned container network '${LAB_NETWORK_NAME}'"
        run_container network rm "${LAB_NETWORK_NAME}" >/dev/null
    fi

    local kubeconfig_path
    for cluster_name in "${PROFILE_CLUSTER_NAMES[@]}"; do
        kubeconfig_path="$(kubeconfig_path_for "${cluster_name}")"
        rm -f -- "${kubeconfig_path}" "${kubeconfig_path}.tmp"
    done
    rm -f -- "${LAB_STATE_FILE}" "${LAB_STATE_FILE}.tmp"
    rm -f -- "${LAB_REGISTRIES_CONFIG}" "${LAB_REGISTRIES_CONFIG}.tmp"
    rmdir -- "${LAB_KUBECONFIG_DIR}" 2>/dev/null || true
    info "Pole Kubernetes lab '${PROFILE}' is down."
}

resolve_worktree_id() {
    local configured="${POLE_TEST_WORKTREE_ID:-}"
    if [[ -n "${configured}" ]]; then
        configured="${configured,,}"
        [[ "${configured}" =~ ^[a-z0-9]([a-z0-9-]{0,17}[a-z0-9])?$ ]] \
            || die "POLE_TEST_WORKTREE_ID must be 1-19 lowercase alphanumeric or hyphen characters and start and end with an alphanumeric character."
        printf '%s' "${configured}"
        return
    fi

    local dot_git="${LAB_REPOSITORY_ROOT}/.git"
    local git_directory
    if [[ -d "${dot_git}" ]]; then
        git_directory="${dot_git}"
    elif [[ -f "${dot_git}" ]]; then
        local marker
        marker="$(<"${dot_git}")"
        [[ "${marker}" == gitdir:* ]] || die "Git worktree marker '${dot_git}' is malformed."
        git_directory="${marker#gitdir:}"
        git_directory="${git_directory#${git_directory%%[![:space:]]*}}"
        if [[ "${git_directory}" != /* ]]; then
            git_directory="${LAB_REPOSITORY_ROOT}/${git_directory}"
        fi
        git_directory="$(realpath -m -- "${git_directory}")"
    else
        die "Repository Git metadata was not found beneath '${LAB_REPOSITORY_ROOT}'."
    fi

    printf '%s' "${git_directory}" | sha256sum | cut -c1-12
}

audit_test_resources() {
    local report="${1:-true}"
    local worktree_id
    worktree_id="$(resolve_worktree_id)"
    local selector="pole.dev/owned-by=pole-tests,pole.dev/worktree-id=${worktree_id}"
    local namespace_prefix="pole-${worktree_id}-"
    local leaks_found=false
    local index
    for ((index = 0; index < ${#PROFILE_CLUSTER_NAMES[@]}; index++)); do
        local kubeconfig_path
        kubeconfig_path="$(kubeconfig_path_for "${PROFILE_CLUSTER_NAMES[index]}")"
        local context
        context="$(expected_context_for "${PROFILE_CLUSTER_NAMES[index]}")"
        local namespaces
        namespaces="$("${KUBECTL_BIN}" --kubeconfig "${kubeconfig_path}" --context "${context}" \
            get namespaces --selector "${selector}" --output json \
            | jq --raw-output '.items[].metadata.name')"
        local persistent_volumes
        persistent_volumes="$("${KUBECTL_BIN}" --kubeconfig "${kubeconfig_path}" --context "${context}" \
            get persistentvolumes --output json \
            | jq --raw-output --arg prefix "${namespace_prefix}" \
                '.items[] | select((.spec.claimRef.namespace // "") | startswith($prefix)) | .metadata.name')"
        if [[ -n "${namespaces}" ]]; then
            if [[ "${report}" == true ]]; then
                warn "Target '${PROFILE_TARGET_KEYS[index]}' has test namespaces for worktree '${worktree_id}': ${namespaces//$'\n'/, }."
            fi
            leaks_found=true
        fi
        if [[ -n "${persistent_volumes}" ]]; then
            if [[ "${report}" == true ]]; then
                warn "Target '${PROFILE_TARGET_KEYS[index]}' has test persistent volumes for worktree '${worktree_id}': ${persistent_volumes//$'\n'/, }."
            fi
            leaks_found=true
        fi
    done
    local test_containers
    test_containers="$(run_container ps --all \
        --filter "label=pole.dev/owned-by=pole-tests" \
        --filter "label=pole.dev/worktree-id=${worktree_id}" \
        --format '{{.Names}}')"
    if [[ -n "${test_containers}" ]]; then
        if [[ "${report}" == true ]]; then
            warn "Container runtime has test containers for worktree '${worktree_id}': ${test_containers//$'\n'/, }."
        fi
        leaks_found=true
    fi

    if [[ "${leaks_found}" == true ]]; then
        return 1
    fi
    [[ "${report}" == true ]] && info "No test resources remain for worktree '${worktree_id}'."
    return 0
}

audit_tests() {
    prepare_runtime
    validate_state_contract
    [[ "$(jq --raw-output '.phase' "${LAB_STATE_FILE}")" == ready ]] \
        || die "Lab is not ready; audit-tests requires a ready lab."
    local attempt
    for attempt in {1..30}; do
        if audit_test_resources false; then
            audit_test_resources true
            return
        fi
        [[ "${attempt}" -lt 30 ]] || break
        sleep 2
    done
    audit_test_resources true \
        || die "Test resource audit found leftovers after one minute."
}

clean_tests() {
    prepare_runtime
    validate_state_contract
    [[ "$(jq --raw-output '.phase' "${LAB_STATE_FILE}")" == ready ]] \
        || die "Lab is not ready; clean-tests requires a ready lab."
    local worktree_id
    worktree_id="$(resolve_worktree_id)"
    local selector="pole.dev/owned-by=pole-tests,pole.dev/worktree-id=${worktree_id}"
    local index
    for ((index = 0; index < ${#PROFILE_CLUSTER_NAMES[@]}; index++)); do
        local kubeconfig_path
        kubeconfig_path="$(kubeconfig_path_for "${PROFILE_CLUSTER_NAMES[index]}")"
        local context
        context="$(expected_context_for "${PROFILE_CLUSTER_NAMES[index]}")"
        info "Deleting owned test namespaces for worktree '${worktree_id}' on '${PROFILE_TARGET_KEYS[index]}'"
        "${KUBECTL_BIN}" --kubeconfig "${kubeconfig_path}" --context "${context}" \
            delete namespaces --selector "${selector}" --wait=true --timeout=120s
    done
    local namespace_prefix="pole-${worktree_id}-"
    local attempt
    for attempt in {1..30}; do
        local remaining=false
        for ((index = 0; index < ${#PROFILE_CLUSTER_NAMES[@]}; index++)); do
            local kubeconfig_path
            kubeconfig_path="$(kubeconfig_path_for "${PROFILE_CLUSTER_NAMES[index]}")"
            local context
            context="$(expected_context_for "${PROFILE_CLUSTER_NAMES[index]}")"
            if "${KUBECTL_BIN}" --kubeconfig "${kubeconfig_path}" --context "${context}" \
                get persistentvolumes --output json \
                | jq --exit-status --arg prefix "${namespace_prefix}" \
                    'any(.items[]; (.spec.claimRef.namespace // "") | startswith($prefix))' >/dev/null; then
                remaining=true
            fi
        done
        [[ "${remaining}" == false ]] && break
        [[ "${attempt}" -lt 30 ]] || die "Test persistent volumes for worktree '${worktree_id}' were not reclaimed within one minute."
        sleep 2
    done
    local -a container_ids=()
    mapfile -t container_ids < <(run_container ps --all \
        --filter "label=pole.dev/owned-by=pole-tests" \
        --filter "label=pole.dev/worktree-id=${worktree_id}" \
        --format '{{.ID}}')
    local container_id
    for container_id in "${container_ids[@]}"; do
        [[ -n "${container_id}" ]] || continue
        local owner
        owner="$(run_container inspect "${container_id}" --format '{{index .Config.Labels "pole.dev/owned-by"}}')"
        local container_worktree_id
        container_worktree_id="$(run_container inspect "${container_id}" --format '{{index .Config.Labels "pole.dev/worktree-id"}}')"
        [[ "${owner}" == pole-tests && "${container_worktree_id}" == "${worktree_id}" ]] \
            || die "Refusing to delete container '${container_id}' without the exact Pole test ownership labels."
        info "Deleting owned test container '${container_id}' for worktree '${worktree_id}'"
        run_container rm --force "${container_id}" >/dev/null
    done
    audit_test_resources true \
        || die "Test resource audit found leftovers after recovery cleanup."
}

reset_lab() {
    validate_state_root
    local -a requested_arguments=("$@")
    if [[ "${#requested_arguments[@]}" -eq 0 ]] && [[ -f "${LAB_STATE_FILE}" ]]; then
        local existing_profile
        existing_profile="$(read_state_profile)"
        requested_arguments+=("${existing_profile}")
        if [[ "${existing_profile}" != full ]]; then
            local overlay
            while IFS= read -r overlay; do
                requested_arguments+=(--overlay "${overlay}")
            done < <(jq --raw-output '.overlays[]?' "${LAB_STATE_FILE}")
        fi
    fi
    if [[ -f "${LAB_STATE_FILE}" ]]; then
        down_lab
    fi
    up_lab "${requested_arguments[@]}"
}

acquire_lifecycle_lock() {
    validate_state_root
    need flock
    mkdir -p -- "${LAB_STATE_ROOT}"
    exec 9>"${LAB_STATE_ROOT}/lifecycle.lock"
    flock --nonblock 9 || die "Another Pole Kubernetes lab command is already running."
}

main() {
    local command="${1:-}"
    case "${command}" in
        up)
            acquire_lifecycle_lock
            shift
            up_lab "$@"
            ;;
        status)
            acquire_lifecycle_lock
            shift
            [[ "$#" -eq 0 ]] || die "The status command does not accept a profile."
            status_lab
            ;;
        verify)
            acquire_lifecycle_lock
            shift
            [[ "$#" -eq 0 ]] || die "The verify command does not accept a profile."
            trap cleanup_verification_namespaces EXIT
            verify_lab
            ;;
        audit-tests)
            acquire_lifecycle_lock
            shift
            [[ "$#" -eq 0 ]] || die "The audit-tests command does not accept arguments."
            audit_tests
            ;;
        clean-tests)
            acquire_lifecycle_lock
            shift
            [[ "$#" -eq 0 ]] || die "The clean-tests command does not accept arguments."
            clean_tests
            ;;
        reset)
            acquire_lifecycle_lock
            shift
            reset_lab "$@"
            ;;
        down)
            acquire_lifecycle_lock
            shift
            [[ "$#" -eq 0 ]] || die "The down command does not accept a profile."
            down_lab
            ;;
        -h|--help|help)
            usage
            ;;
        *)
            usage >&2
            exit 2
            ;;
    esac
}

main "$@"
