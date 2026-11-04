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
#   check             what a first provision needs (Hetzner token, server type in the location, SSH key, age recipient, pull token),
#                     checked without creating or changing anything
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
# No defaults: the reserved IPv4 is made in the location and can't move to another, so a provision without them stops (infrastructure).
location=${PREVIEW_LOCATION:-}
server_type=${PREVIEW_SERVER_TYPE:-}
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
  local ip_location
  [ -n "$location" ] && [ -n "$server_type" ] || fail "Set PREVIEW_LOCATION and PREVIEW_SERVER_TYPE in the Environment preview first: the reserved IPv4 can't move between locations."
  hcloud ssh-key describe "$name" >/dev/null 2>&1 || hcloud ssh-key create --name "$name" --public-key "$(ssh-keygen -y -f "$key")" >/dev/null
  hcloud primary-ip describe "$name" >/dev/null 2>&1 || hcloud primary-ip create --name "$name" --type ipv4 --location "$location" --auto-delete=false >/dev/null
  ip_location=$(hcloud primary-ip describe "$name" -o json | jq -r .location.name)   # location: Hetzner removed datacenter from Primary IPs, 1 July 2026
  [ "$ip_location" = "$location" ] || fail "The reserved IPv4 is in $ip_location, not $location, and can't move: set PREVIEW_LOCATION=$ip_location, or delete the address in Hetzner's console (and repoint the DNS records)."
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
# helm reads the cluster from k3s.yaml, given each time: unlike k3s's kubectl, it doesn't look there itself.
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

# The console's image is private: a pull secret for ghcr.io from the read-only token, handed to the cluster on stdin.
pull_secret() {
  : "${PREVIEW_CONSOLE_PULL_TOKEN:?the pull token for the console image, a classic PAT with read:packages only}"
  mask "$PREVIEW_CONSOLE_PULL_TOKEN"
  jq -nc --arg user "${PREVIEW_CONSOLE_PULL_USER:-babs76}" --arg token "$PREVIEW_CONSOLE_PULL_TOKEN" \
    '{auths: {"ghcr.io": {auth: ("\($user):\($token)" | @base64)}}}' \
    | on_server "kubectl create secret generic ghcr --type=kubernetes.io/dockerconfigjson --from-file=.dockerconfigjson=/dev/stdin --dry-run=client -o yaml | kubectl apply -f - >/dev/null"
}

# The API reads Keycloak's keys inside the cluster, not through the public address and its certificate; its tokens' issuer stays the public one.
chart() {
  tar -C deploy -cz helm/insys preview/values.yaml preview/backup.yaml openbao/openbao.sh keycloak/realm-name.sh | on_server "rm -rf /root/insys && mkdir -p /root/insys && tar -C /root/insys -xz"
  on_server "cd /root/insys && KUBECONFIG=/etc/rancher/k3s/k3s.yaml helm upgrade --install insys helm/insys -f preview/values.yaml \
    --set ingress.host=$PREVIEW_DOMAIN,ingress.apiPath=/api/,console.environmentName=preview,keycloak.ingressHost=id.$PREVIEW_DOMAIN,keycloak.hostname=https://id.$PREVIEW_DOMAIN \
    --set authentication.authority=http://insys-keycloak:8080/realms/insys,authentication.requireHttpsMetadata=false \
    --set authentication.issuer=https://id.$PREVIEW_DOMAIN/realms/insys,console.issuer=https://id.$PREVIEW_DOMAIN/realms/insys"
  on_server "kubectl wait --for=condition=complete job -l app.kubernetes.io/name=migrate --timeout=900s && kubectl rollout status statefulset/insys-openbao --timeout=600s"
  on_server "kubectl apply -f /root/insys/preview/backup.yaml >/dev/null"
  realm_name
}

# The sign-in page's name, from the values, on an existing realm too: the chart's import names only a new one. The admin password stays on the server.
realm_name() {
  local label   # not name: that's the server's, which on_server reads
  label=$(sed -n 's/^keycloak:.*realmDisplayName: "\([^"]*\)".*/\1/p' deploy/preview/values.yaml)
  [ -n "$label" ] || fail "deploy/preview/values.yaml sets no keycloak.realmDisplayName"
  on_server "kubectl rollout status deploy/insys-keycloak --timeout=900s >/dev/null && kubectl get secret insys-secrets -o jsonpath='{.data.keycloakAdminPassword}' \
    | base64 -d | /root/insys/keycloak/realm-name.sh insys insys $(printf '%q' "$label")"
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

# The API's and Keycloak's port-forwards on the server, once both have rolled out, each waited for until it answers (a fixed sleep raced them:
# rotate-passwords run 41929744851 got curl's empty reply, 52). If one doesn't answer, the pods' states and the forwards' own errors say why.
forwards() {
  # shellcheck disable=SC2016 # it runs on the server, which expands it
  on_server 'kubectl rollout status deploy/insys-keycloak --timeout=600s >/dev/null && kubectl rollout status deploy/insys-api --timeout=600s >/dev/null \
      || { kubectl get pods -o wide; exit 1; }
    pkill -f "^kubectl port-forward svc/insys-" || true
    (nohup kubectl port-forward svc/insys-api 18080:8080 >/tmp/forward-api.log 2>&1 &)
    (nohup kubectl port-forward svc/insys-keycloak 18081:8080 >/tmp/forward-keycloak.log 2>&1 &)
    for port in 18080 18081; do
      for _ in $(seq 1 60); do [ "$(curl -s -o /dev/null -w "%{http_code}" "http://127.0.0.1:$port/")" != 000 ] && continue 2; sleep 1; done
      echo "The port-forward on $port does not answer."; kubectl get pods -o wide; tail -n 5 /tmp/forward-api.log /tmp/forward-keycloak.log; exit 1
    done' || fail "the API's or Keycloak's port-forward on the server didn't answer: see the pods above"
}

# The seed and the accounts, through tunnels to the API and Keycloak. The accounts' passwords come back on the seed's stdout only.
# "^kubectl port-forward": anchored, so pkill -f matches only the forwards, not the remote shell running it, whose command line holds the same text.
seed() {
  local admin
  admin=$(on_server "kubectl get secret insys-secrets -o jsonpath='{.data.keycloakAdminPassword}' | base64 -d")
  mask "$admin"
  forwards
  ssh -i "$key" -o UserKnownHostsFile="$RUNNER_TEMP/known_hosts" -fN -L 18080:127.0.0.1:18080 -L 18081:127.0.0.1:18081 "root@$(address)"
  for port in 18080 18081; do   # the runner's end of the tunnel too: any HTTP answer will do
    for _ in $(seq 1 30); do [ "$(curl -s -o /dev/null -w '%{http_code}' "http://127.0.0.1:$port/")" != 000 ] && continue 2; sleep 1; done
    fail "the tunnel to port $port on the server doesn't answer"
  done
  clients "$admin"
  dotnet build tools/Insys.Seed -c Release -v q >/dev/null
  ACCOUNTS=$(INSYS_SEED_KEYCLOAK_ADMIN_PASSWORD="$admin" dotnet tools/Insys.Seed/bin/Release/net10.0/Insys.Seed.dll --api http://127.0.0.1:18080 \
    --keycloak-url http://127.0.0.1:18081 --keycloak-admin admin --operator-tenant "$operator_tenant" --platform-1 insys-admin --platform-2 insys-admin-2 --accounts true) || {
    on_server "kubectl logs deploy/insys-api --tail=400 | grep -E 'IDX[0-9]+|Bearer|metadata|SSL|certificate' | tail -n 20" || true   # why, from the API: IDX messages hide the token
    fail "the seed failed"
  }
  while read -r password; do mask "$password"; done < <(jq -r '.accounts[].password' <<<"$ACCOUNTS")
  on_server "pkill -f '^kubectl port-forward svc/insys-' || true"
}

# Each app's Keycloak client gets the Preview's addresses, through the tunnel: sign-in returns to the app, and the browser may call the token endpoint.
clients() {
  local token id base
  # The password on curl's stdin and the token in a header file, never on a command line.
  token=$(printf '%s' "$1" | curl -sf -d grant_type=password -d client_id=admin-cli -d username=admin --data-urlencode password@- \
    http://127.0.0.1:18081/realms/master/protocol/openid-connect/token | jq -r .access_token)
  mask "$token"
  for client in insys-web:/ broker-portal:/broker/ member-portal:/member/ provider-portal:/provider/; do
    base="https://$PREVIEW_DOMAIN${client#*:}"
    id=$(curl -sf -H @<(printf 'Authorization: Bearer %s\n' "$token") "http://127.0.0.1:18081/admin/realms/insys/clients?clientId=${client%%:*}" | jq -r '.[0].id')
    curl -sf -H @<(printf 'Authorization: Bearer %s\n' "$token") "http://127.0.0.1:18081/admin/realms/insys/clients/$id" \
      | jq --arg base "$base" --arg origin "https://$PREVIEW_DOMAIN" '.redirectUris = [$base + "*"] | .webOrigins = [$origin] | .rootUrl = $base | .attributes["post.logout.redirect.uris"] = ($base + "*")' \
      | curl -sf -X PUT -H @<(printf 'Authorization: Bearer %s\n' "$token") -H 'Content-Type: application/json' --data-binary @- "http://127.0.0.1:18081/admin/realms/insys/clients/$id"
  done
}

# Encrypted to Ade's key, the only form that leaves memory. Written once OpenBao is initialised too, so a later failure keeps its keys.
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
    case "$issuer" in *"Let's Encrypt"*|*"(STAGING)"*) ;; *)
      on_server "kubectl -n kube-system logs deploy/traefik --tail=300 | grep -iE 'acme|certificate|resolver' | tail -n 20" || true   # why, from Traefik: no secret in it
      fail "$host has no Let's Encrypt certificate yet: are its A records in place?" ;; esac
  done
}

# What a first provision needs, checked without creating or changing anything: pass or fail per item, and no secret in the output. Each item
# returns its own status, as set -e doesn't apply inside the if that runs it.
check() {
  local failed=0 out
  exec 3>&1   # the step's output, for what an item must say outside its captured result
  item() { # label, then the check; its output says what was found
    local label=$1; shift
    if out=$("$@" 2>&1); then echo "PASS  $label: $out"; else echo "FAIL  $label: ${out:-no detail}"; failed=1; fi
  }
  item "Hetzner token" check_token
  item "$server_type in $location" check_server
  item "PREVIEW_SSH_KEY" check_ssh_key
  item "PREVIEW_AGE_RECIPIENT" check_age
  item "PREVIEW_CONSOLE_PULL_TOKEN" check_pull
  [ "$failed" = 0 ] || fail "At least one check failed: see FAIL above."
  echo "Every check passed. Nothing was created or changed."
}
check_token() {
  hcloud location list -o noheader -o columns=name >/dev/null || return 1
  echo "the API answers to it"
}
check_server() {
  local found
  [ -n "$location" ] && [ -n "$server_type" ] || { echo "PREVIEW_LOCATION and PREVIEW_SERVER_TYPE must both be set"; return 1; }
  # The server type's own locations, each with available: Hetzner's indicator of what can be created now, not a guarantee (changelog,
  # 1 April 2026; the datacenters' server_types it replaced are gone). Straight from the API, the token on curl's stdin.
  found=$(printf 'header = "Authorization: Bearer %s"\n' "$HCLOUD_TOKEN" \
    | curl -sS --fail-with-body -K - "https://api.hetzner.cloud/v1/server_types?name=$server_type" \
    | jq -r --arg l "$location" '.server_types[0] | if . == null then "no-type"
        else ([.locations[]? | select(.name == $l)][0]) as $at
          | if $at == null then "not-offered" elif $at.available then "yes" else "no" end end') || return 1
  case "$found" in
    yes) echo "available now (Hetzner's indicator, not a guarantee)" ;;
    no) echo "offered in $location but not available now"; return 1 ;;
    not-offered) echo "not offered in $location"; return 1 ;;
    *) echo "no server type $server_type"; return 1 ;;
  esac
}
check_ssh_key() {
  local file fingerprint
  [ -n "${PREVIEW_SSH_KEY:-}" ] || { echo "not set"; return 1; }
  file=$(mktemp "$RUNNER_TEMP/check-key.XXXXXX")
  printf '%s\n' "$PREVIEW_SSH_KEY" > "$file"
  # The public half's fingerprint and type only: public, and what Ade can match against the key on his machine.
  fingerprint=$(ssh-keygen -y -P '' -f "$file" 2>/dev/null | ssh-keygen -l -f - 2>/dev/null)
  rm -f "$file"
  [ -n "$fingerprint" ] || { echo "doesn't parse as an unencrypted private key"; return 1; }
  echo "a private key; its public half is $(awk '{print $NF, $2}' <<<"$fingerprint")"
}
check_age() {
  [ -n "${PREVIEW_AGE_RECIPIENT:-}" ] || { echo "not set"; return 1; }
  age -r "$PREVIEW_AGE_RECIPIENT" -o /dev/null </dev/null 2>/dev/null || { echo "age refuses it as a recipient"; return 1; }
  echo "age encrypts to it"
}
check_pull() {
  local image repo bearer code
  [ -n "${PREVIEW_CONSOLE_PULL_TOKEN:-}" ] || { echo "not set"; return 1; }
  image=$(sed -n 's/^  image: \(ghcr.io\/[^ ]*@sha256:[0-9a-f]*\).*/\1/p' "$here/../helm/insys/values.yaml")
  repo=${image#ghcr.io/}; repo=${repo%@*}
  # The token goes to curl as a config on stdin, not on its command line.
  bearer=$(printf 'user = "%s:%s"\n' "${PREVIEW_CONSOLE_PULL_USER:-babs76}" "$PREVIEW_CONSOLE_PULL_TOKEN" \
    | curl -s -K - "https://ghcr.io/token?scope=repository:$repo:pull" | jq -r '.token // empty')
  [ -n "$bearer" ] || { echo "ghcr.io gave no token for it (user ${PREVIEW_CONSOLE_PULL_USER:-babs76})"; return 1; }
  mask "$bearer" >&3   # to the step's own output: a mask is a workflow command only at the start of a line
  code=$(printf 'header = "Authorization: Bearer %s"\n' "$bearer" | curl -s -K - -o /dev/null -w '%{http_code}' \
    -H 'Accept: application/vnd.oci.image.index.v1+json, application/vnd.oci.image.manifest.v1+json, application/vnd.docker.distribution.manifest.v2+json' \
    "https://ghcr.io/v2/$repo/manifests/${image#*@}")
  [ "$code" = 200 ] || { echo "the pinned manifest answers HTTP $code"; return 1; }
  echo "reads the pinned manifest, $repo@${image#*@}"
}

if [ "$action" = check ]; then
  check
  exit 0
fi

trap close_ssh EXIT
case "$action" in
  provision)
    ssh_key; infrastructure; open_ssh "${RUNNER_IP:?}"; wait_for_ssh; cluster; images; server_secrets; pull_secret; chart; openbao; encrypt; seed; encrypt; check_tls
    ;;
  upgrade)
    ssh_key; open_ssh "${RUNNER_IP:?}"; wait_for_ssh; cluster; images; pull_secret; chart; check_tls
    ;;
  open-ssh)
    trap - EXIT
    open_ssh "${2:?the address to allow}"
    echo "SSH is open from $2 until the next run closes it."
    ;;
  reset)
    ssh_key; open_ssh "${RUNNER_IP:?}"; wait_for_ssh
    on_server "KUBECONFIG=/etc/rancher/k3s/k3s.yaml helm uninstall insys --wait || true; kubectl delete pvc --all --wait=true; kubectl delete secret insys-secrets --ignore-not-found"
    cluster; images; server_secrets; pull_secret; chart; openbao; encrypt; seed; encrypt; check_tls
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
