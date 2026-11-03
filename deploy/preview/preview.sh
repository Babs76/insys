#!/usr/bin/env bash
# ============================================================================
# OPENFACTOR TECHNOLOGY GROUP
# CONFIDENTIAL AND PROPRIETARY SOFTWARE
# ============================================================================
# Copyright (c) 2026 OpenFactor Technology Group.
# All rights reserved.
# PRODUCT       : INSYS
# COMPONENT     : Build
# CONFIDENTIALITY AND INTELLECTUAL PROPERTY NOTICE
# This source code and its associated documentation contain confidential and
# proprietary information belonging to OpenFactor Technology Group or its
# applicable intellectual property rights holder.
# This material is provided solely for authorised development, maintenance,
# operation or evaluation purposes, as applicable. No person may reproduce,
# modify, distribute, disclose, publish or otherwise use this material except
# as expressly permitted by an applicable written agreement or licence.
# No ownership rights or licence to any intellectual property are granted
# by the inclusion of this notice, except as expressly provided in an
# applicable written agreement or licence.
# Third-party software, open-source components and customer-owned materials
# remain subject to their respective ownership rights and applicable licences
# or agreements.
# UNAUTHORISED ACCESS, USE, COPYING OR DISCLOSURE IS PROHIBITED.
# ============================================================================
# The INSYS Preview's actions (docs/plans/insys-preview.md), run by .github/workflows/preview.yml on a GitHub runner:
#   provision         the server, its firewall and address, k3s, the images, the chart, OpenBao, the seed and the accounts
#   upgrade           the images and the chart at this ref; data and passwords stay
#   open-ssh <ip>     SSH from that address, until the next run closes it (for Ade to unseal OpenBao)
#   reset             everything removed and made again from the seed, with new passwords and a new OpenBao
#   rotate-passwords  new passwords for the accounts
#   destroy           the server and its firewall; the address is kept, so the DNS records stay right
#
# Secrets: Hetzner's token and the SSH key come from the environment, set by Ade in GitHub. The server's own secrets are generated on the
# server's first provision and stay there. Passwords, OpenBao's unseal key and root token exist only in memory here, until encrypted to Ade's
# age key into $RUNNER_TEMP/preview-secrets.age; they are masked in the log and never on a command line or in a file in clear.
set -euo pipefail
here="$(cd "$(dirname "$0")" && pwd)"
action=${1:?an action}
: "${HCLOUD_TOKEN:?}" "${PREVIEW_DOMAIN:?}"
name=insys-preview
location=${PREVIEW_LOCATION:-nbg1}
server_type=${PREVIEW_SERVER_TYPE:-cx43}
k3s_version=${PREVIEW_K3S_VERSION:-v1.33.5+k3s1}
helm_version=v3.19.0
operator_tenant=0199f000-0000-7000-8000-000000000001
key="$RUNNER_TEMP/preview-ssh"
out="$RUNNER_TEMP/preview-secrets.age"

mask() { echo "::add-mask::$1"; }
fail() { echo "::error::$*" >&2; exit 1; }

ssh_key() {
  : "${PREVIEW_SSH_KEY:?}"
  install -m 600 /dev/null "$key"
  printf '%s\n' "$PREVIEW_SSH_KEY" > "$key"
}

address() { hcloud primary-ip describe "$name" -o format='{{.IP}}'; }
on_server() { ssh -i "$key" -o StrictHostKeyChecking=accept-new -o UserKnownHostsFile="$RUNNER_TEMP/known_hosts" -o ConnectTimeout=20 "root@$(address)" "$@"; }

# SSH is closed except during a run: only 443 is open.
close_ssh() {
  hcloud firewall describe "$name" >/dev/null 2>&1 || return 0
  printf '[{"direction":"in","protocol":"tcp","port":"443","source_ips":["0.0.0.0/0","::/0"],"description":"HTTPS"}]' > "$RUNNER_TEMP/rules.json"
  hcloud firewall replace-rules "$name" --rules-file "$RUNNER_TEMP/rules.json" >/dev/null
}

open_ssh() {
  hcloud firewall add-rule "$name" --direction in --protocol tcp --port 22 --source-ips "$1/32" --description "SSH for run ${GITHUB_RUN_ID:-local}" >/dev/null
}

infrastructure() {
  hcloud ssh-key describe "$name" >/dev/null 2>&1 || hcloud ssh-key create --name "$name" --public-key "$(ssh-keygen -y -f "$key")" >/dev/null
  hcloud primary-ip describe "$name" >/dev/null 2>&1 || hcloud primary-ip create --name "$name" --type ipv4 --location "$location" --auto-delete=false >/dev/null
  hcloud firewall describe "$name" >/dev/null 2>&1 || hcloud firewall create --name "$name" >/dev/null
  close_ssh
  if ! hcloud server describe "$name" >/dev/null 2>&1; then
    hcloud server create --name "$name" --type "$server_type" --image ubuntu-24.04 --location "$location" --ssh-key "$name" \
      --firewall "$name" --primary-ipv4 "$name" --without-ipv6 --enable-backup >/dev/null
  fi
  echo "The server's address is $(address). Point both A records at it: $PREVIEW_DOMAIN and id.$PREVIEW_DOMAIN."
}

wait_for_ssh() {
  for _ in $(seq 1 60); do on_server true 2>/dev/null && return 0; sleep 5; done
  fail "the server didn't answer on SSH"
}

# k3s with its bundled Traefik, which gets Let's Encrypt on 443; helm and jq for the steps run on the server.
cluster() {
  local ca=https://acme-staging-v02.api.letsencrypt.org/directory
  [ "${PREVIEW_ACME:-staging}" = production ] && ca=https://acme-v02.api.letsencrypt.org/directory
  on_server "command -v k3s >/dev/null || curl -sfL https://get.k3s.io | INSTALL_K3S_VERSION='$k3s_version' sh -s - --write-kubeconfig-mode 600"
  on_server "command -v jq >/dev/null || (apt-get update -qq && apt-get install -y -qq jq >/dev/null)"
  on_server "command -v helm >/dev/null || (curl -sfL https://get.helm.sh/helm-$helm_version-linux-amd64.tar.gz | tar -xz -C /tmp && install /tmp/linux-amd64/helm /usr/local/bin/helm)"
  sed -e "s#ACME_CA#$ca#" -e "s#ACME_EMAIL#${PREVIEW_ACME_EMAIL:-}#" "$here/traefik.yaml" | on_server "cat > /var/lib/rancher/k3s/server/manifests/traefik-config.yaml"
  on_server "until kubectl get nodes 2>/dev/null | grep -q ' Ready'; do sleep 5; done"
}

images() {
  docker build -q -f deploy/docker/api.Dockerfile -t insys-api:preview . >/dev/null
  docker build -q -f deploy/docker/migrator.Dockerfile -t insys-migrator:preview . >/dev/null
  docker save insys-api:preview insys-migrator:preview | on_server "k3s ctr images import -" >/dev/null
}

# The server's own secrets, generated once on the server and kept only in its Secret. The API's OpenBao token comes later.
server_secrets() {
  # shellcheck disable=SC2016 # it runs on the server, which expands it
  on_server 'kubectl get secret insys-secrets >/dev/null 2>&1 && exit 0
    r() { head -c 32 /dev/urandom | base64 | tr -d "/+=" | head -c 32; }
    owner=$(r); role=$(r)
    kubectl create secret generic insys-secrets \
      --from-literal=migrationConnection="Host=insys-postgres;Database=insys;Username=insys_owner;Password=$owner" \
      --from-literal=connectionTemplate="Host=insys-postgres;Database=insys;Username={role};Password=$role" \
      --from-literal=rolePassword="$role" --from-literal=postgresPassword="$owner" \
      --from-literal=hashKey="$(head -c 32 /dev/urandom | base64)" --from-literal=kmsToken=pending \
      --from-literal=objectStoreAccessKey="$(r)" --from-literal=objectStoreSecretKey="$(r)" \
      --from-literal=keycloakAdminPassword="$(r)" >/dev/null'
}

chart() {
  tar -C deploy -cz helm/insys preview/values.yaml preview/backup.yaml openbao/openbao.sh | on_server "rm -rf /root/insys && mkdir -p /root/insys && tar -C /root/insys -xz"
  on_server "cd /root/insys && helm upgrade --install insys helm/insys -f preview/values.yaml \
    --set ingress.host=$PREVIEW_DOMAIN,ingress.apiPath=/api/,keycloak.ingressHost=id.$PREVIEW_DOMAIN,keycloak.hostname=https://id.$PREVIEW_DOMAIN \
    --set authentication.authority=https://id.$PREVIEW_DOMAIN/realms/insys"
  on_server "kubectl wait --for=condition=complete job -l app.kubernetes.io/name=migrate --timeout=900s && kubectl rollout status statefulset/insys-openbao --timeout=600s"
  on_server "kubectl apply -f /root/insys/preview/backup.yaml >/dev/null"
}

# OpenBao made new: initialised, unsealed and configured. Its unseal key and root token go only into the encrypted file.
openbao() {
  local init token
  init=$(on_server "cd /root/insys && openbao/openbao.sh init insys")
  mask "$(jq -r '.keys_base64[0]' <<<"$init")"; mask "$(jq -r .root_token <<<"$init")"
  jq -r '.keys_base64[0]' <<<"$init" | on_server "cd /root/insys && openbao/openbao.sh unseal insys"
  token=$(jq -r .root_token <<<"$init" | on_server "cd /root/insys && openbao/openbao.sh configure insys transit insys")
  mask "$token"
  jq -nc --arg t "$token" '{stringData: {kmsToken: $t}}' | on_server "kubectl patch secret insys-secrets --type merge --patch-file /dev/stdin >/dev/null"
  on_server "kubectl rollout restart deploy/insys-api && kubectl rollout status deploy/insys-api --timeout=600s && kubectl rollout status deploy/insys-keycloak --timeout=900s"
  OPENBAO_INIT=$init
}

# The seed and the accounts, through tunnels to the API and Keycloak. The accounts' passwords come back on the seed's stdout only.
seed() {
  local admin
  admin=$(on_server "kubectl get secret insys-secrets -o jsonpath='{.data.keycloakAdminPassword}' | base64 -d")
  mask "$admin"
  on_server "pkill -f 'port-forward svc/insys-' || true; (nohup kubectl port-forward svc/insys-api 18080:8080 >/dev/null 2>&1 &); (nohup kubectl port-forward svc/insys-keycloak 18081:8080 >/dev/null 2>&1 &); sleep 3"
  ssh -i "$key" -o UserKnownHostsFile="$RUNNER_TEMP/known_hosts" -fN -L 18080:127.0.0.1:18080 -L 18081:127.0.0.1:18081 "root@$(address)"
  dotnet build tools/Insys.Seed -c Release -v q >/dev/null
  ACCOUNTS=$(INSYS_SEED_KEYCLOAK_ADMIN_PASSWORD="$admin" dotnet tools/Insys.Seed/bin/Release/net10.0/Insys.Seed.dll --api http://127.0.0.1:18080 \
    --keycloak-url http://127.0.0.1:18081 --keycloak-admin admin --operator-tenant "$operator_tenant" --platform-1 insys-admin --platform-2 insys-admin-2 --accounts true)
  while read -r password; do mask "$password"; done < <(jq -r '.accounts[].password' <<<"$ACCOUNTS")
  on_server "pkill -f 'port-forward svc/insys-' || true"
}

# Encrypted to Ade's key, the only form that leaves memory.
encrypt() {
  : "${PREVIEW_AGE_RECIPIENT:?}"
  jq -n --argjson accounts "${ACCOUNTS:-null}" --argjson openbao "${OPENBAO_INIT:-null}" --arg domain "$PREVIEW_DOMAIN" \
    '{domain: $domain, accounts: $accounts.accounts, openbao: ($openbao | if . then {unsealKey: .keys_base64[0], rootToken: .root_token} else null end)}' \
    | age -r "$PREVIEW_AGE_RECIPIENT" -o "$out"
  echo "Encrypted for Ade: $(basename "$out")."
}

# TLS is proven before anyone relies on it: the certificate must come from the CA asked for.
check_tls() {
  for host in "$PREVIEW_DOMAIN" "id.$PREVIEW_DOMAIN"; do
    for _ in $(seq 1 60); do
      issuer=$(echo | openssl s_client -connect "$(address):443" -servername "$host" 2>/dev/null | openssl x509 -noout -issuer 2>/dev/null || true)
      case "$issuer" in *"Let's Encrypt"*|*"(STAGING)"*) break ;; esac
      sleep 10
    done
    echo "$host: $issuer"
    case "$issuer" in *"Let's Encrypt"*|*"(STAGING)"*) ;; *) fail "$host has no Let's Encrypt certificate yet: are its A records in place?" ;; esac
  done
}

trap close_ssh EXIT
case "$action" in
  provision)
    ssh_key; infrastructure; open_ssh "${RUNNER_IP:?}"; wait_for_ssh; cluster; images; server_secrets; chart; openbao; seed; encrypt; check_tls
    ;;
  upgrade)
    ssh_key; open_ssh "${RUNNER_IP:?}"; wait_for_ssh; cluster; images; chart; check_tls
    ;;
  open-ssh)
    trap - EXIT
    open_ssh "${2:?the address to allow}"
    echo "SSH is open from $2 until the next run closes it."
    ;;
  reset)
    ssh_key; open_ssh "${RUNNER_IP:?}"; wait_for_ssh
    on_server "helm uninstall insys --wait || true; kubectl delete pvc --all --wait=true; kubectl delete secret insys-secrets --ignore-not-found"
    cluster; images; server_secrets; chart; openbao; seed; encrypt; check_tls
    ;;
  rotate-passwords)
    ssh_key; open_ssh "${RUNNER_IP:?}"; wait_for_ssh; seed; encrypt
    ;;
  destroy)
    hcloud server delete "$name" >/dev/null 2>&1 || true
    hcloud firewall delete "$name" >/dev/null 2>&1 || true
    echo "Removed the server and its firewall. The address $(address) is kept for the DNS records; delete it in Hetzner's console if it's no longer wanted."
    ;;
  *)
    fail "unknown action: $action"
    ;;
esac
