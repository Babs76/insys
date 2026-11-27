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
#                     checked without creating anything; once the server exists, also whether OpenBao is sealed, over SSH opened for
#                     the runner only and closed at the end with any other SSH rule
#   provision         the server, its firewall and address, k3s, the images, the chart, OpenBao, the seed and the accounts
#   upgrade           the images and the chart at this ref; data and passwords stay. Then the upgrade check: the run fails if any existing tenant
#                     has an approval type no role holding the authority and the function can decide (Ade, 26 November 2026)
#   upgrade-check     read only: that check alone, against the version serving now
#   read-authority    read only: OFTZA's STATIC_DATA_T1 authority rows, DEMO_CHECKER's authority for the five new types and STATIC_DATA_T1,
#                     and the demo users' roles with the delegation table's functions, in a read-only session (Ade, 26 November 2026)
#   open-ssh <ip>     SSH from that address, until the next run closes it (for Ade to unseal OpenBao); check closes it soonest
#   diagnose          read only: what rebooted the server or restarted k3s (boots, package runs, needrestart, the journal, Hetzner's actions)
#   os-update         the server's packages updated, nothing restarted or rebooted; says what would need a restart or reboot
#   reboot            the server rebooted (the rebuild plan's 0A.2, each run approved by Ade), only after a backup taken today; then a check
#                     that it booted again, on which kernel, that k3s and the pods came back, and what OpenBao reports (sealed, as after any reboot)
#   rotate-ssh-key    PREVIEW_SSH_KEY_NEW put in place of PREVIEW_SSH_KEY: on the server, every other key removed; and in Hetzner
#   keycloak-theme    Keycloak only (BR-168): the console's login theme insys, as synced into the chart's files, mounted in the running
#                     Keycloak as the chart would and set on the realm; then a sign-in, two-factor setup and one-time code check as a
#                     temporary Keycloak-only user, removed at the end. If the check fails, the realm's previous login theme is set back at once
#   keycloak-theme-revert  Keycloak only: the realm's login theme set back to the one keycloak-theme recorded (or Keycloak's default), then the same check
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
summary() { [ -z "${GITHUB_STEP_SUMMARY:-}" ] || printf '%s\n\n' "$*" >> "$GITHUB_STEP_SUMMARY"; }
# Which run initialised the server's OpenBao, and so whose encrypted file holds its unseal key: "<run id> <yyyy-mm-dd>", written by openbao().
openbao_run_note=/var/lib/rancher/k3s/server/insys-openbao-run

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
  on_server "command -v k3s >/dev/null || curl -sfL --retry 3 --retry-all-errors --retry-delay 5 https://get.k3s.io | INSTALL_K3S_VERSION='$k3s_version' sh -s - --write-kubeconfig-mode 600"
  on_server "command -v jq >/dev/null || (apt-get update -qq && apt-get install -y -qq jq >/dev/null)"
  on_server "command -v helm >/dev/null || (curl -sfL --retry 3 --retry-all-errors --retry-delay 5 https://get.helm.sh/helm-$helm_version-linux-amd64.tar.gz | tar -xz -C /tmp && install /tmp/linux-amd64/helm /usr/local/bin/helm)"
  sed -e "s#ACME_CA#$ca#" -e "s#ACME_EMAIL#${PREVIEW_ACME_EMAIL:-}#" "$here/traefik.yaml" | on_server "cat > /var/lib/rancher/k3s/server/manifests/traefik-config.yaml"
  on_server "until kubectl get nodes 2>/dev/null | grep -q ' Ready'; do sleep 5; done"
  acme_ca "$ca"
}

# Traefik keeps its certificates in acme.json when its CA changes: v3.3.2 resets only the ACME account (pkg/provider/acme/provider.go) and
# renews a certificate only in its last 30 days, so a switch from staging to production would go on serving staging's for about two months.
# So the CA the certificates came from is noted on the server, and on a change, once Traefik runs with the new CA, acme.json is emptied and
# Traefik restarted: it then asks the new CA for both hosts. No note means staging, the only CA used before this was written.
acme_ca() {
  on_server "CA='$1' bash -s" <<'EOF'
set -euo pipefail
note=/var/lib/rancher/k3s/server/insys-acme-ca
was=$(cat "$note" 2>/dev/null || echo https://acme-staging-v02.api.letsencrypt.org/directory)
[ "$was" = "$CA" ] && exit 0
for _ in $(seq 1 120); do
  kubectl -n kube-system get deploy traefik -o jsonpath='{.spec.template.spec.containers[0].args}' 2>/dev/null \
    | grep -qF -- "--certificatesresolvers.le.acme.caServer=$CA" && break
  sleep 5
done
kubectl -n kube-system get deploy traefik -o jsonpath='{.spec.template.spec.containers[0].args}' | grep -qF -- "--certificatesresolvers.le.acme.caServer=$CA" \
  || { echo "Traefik didn't get the new CA, $CA, within 10 minutes"; exit 1; }
kubectl -n kube-system rollout status deploy/traefik --timeout=600s >/dev/null
kubectl -n kube-system exec deploy/traefik -- sh -c ': > /data/acme.json'
kubectl -n kube-system rollout restart deploy/traefik >/dev/null
kubectl -n kube-system rollout status deploy/traefik --timeout=600s >/dev/null
printf '%s' "$CA" > "$note"
echo "Traefik's certificates from $was were cleared, so it asks $CA for new ones."
EOF
}

# Each image records its commit (Ade, 27 November 2026): the OCI revision label, a tag with the commit, and, for the API, its
# /health/version. The chart runs the commit's tag, so the release shows it too; preview stays as a second tag.
revision=$(git -C "$here/../.." rev-parse HEAD)
images() {
  docker build -q -f deploy/docker/api.Dockerfile --build-arg REVISION="$revision" -t insys-api:preview -t "insys-api:$revision" . >/dev/null
  docker build -q -f deploy/docker/migrator.Dockerfile --build-arg REVISION="$revision" -t insys-migrator:preview -t "insys-migrator:$revision" . >/dev/null
  docker save insys-api:preview "insys-api:$revision" insys-migrator:preview "insys-migrator:$revision" | on_server "k3s ctr images import -" >/dev/null
  echo "Images built from $revision: insys-api:$revision and insys-migrator:$revision."
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
# The same settings render the theme's ConfigMap for keycloak-theme (theme_ship), so it is what this chart would install.
chart_set="--set ingress.host=$PREVIEW_DOMAIN,ingress.apiPath=/api/,console.environmentName=preview,keycloak.ingressHost=id.$PREVIEW_DOMAIN,keycloak.hostname=https://id.$PREVIEW_DOMAIN \
    --set authentication.authority=http://insys-keycloak:8080/realms/insys,authentication.requireHttpsMetadata=false \
    --set authentication.issuer=https://id.$PREVIEW_DOMAIN/realms/insys,console.issuer=https://id.$PREVIEW_DOMAIN/realms/insys"
chart() {
  tar -C deploy -cz helm/insys preview/values.yaml preview/backup.yaml openbao/openbao.sh keycloak/realm-name.sh keycloak/realm-login-theme.sh keycloak/user-admin-secret.sh keycloak/realm-smtp.sh keycloak/smtp-test.sh | on_server "rm -rf /root/insys && mkdir -p /root/insys && tar -C /root/insys -xz"
  on_server "cd /root/insys && KUBECONFIG=/etc/rancher/k3s/k3s.yaml helm upgrade --install insys helm/insys -f preview/values.yaml \
    $chart_set --set image.api.tag=$revision,image.migrator.tag=$revision"
  on_server "kubectl wait --for=condition=complete job -l app.kubernetes.io/name=migrate --timeout=900s && kubectl rollout status statefulset/insys-openbao --timeout=600s"
  alert_secret
  on_server "kubectl apply -f /root/insys/preview/backup.yaml >/dev/null"
  realm_name
  realm_login_theme
  user_admin
  storage_check
  realm_smtp
  smtp_check
}

# The sign-in page's name, from the values, on an existing realm too: the chart's import names only a new one. The admin password stays on the server.
realm_name() {
  local label   # not name: that's the server's, which on_server reads
  label=$(sed -n 's/^keycloak:.*realmDisplayName: "\([^"]*\)".*/\1/p' deploy/preview/values.yaml)
  [ -n "$label" ] || fail "deploy/preview/values.yaml sets no keycloak.realmDisplayName"
  on_server "kubectl rollout status deploy/insys-keycloak --timeout=900s >/dev/null && kubectl get secret insys-secrets -o jsonpath='{.data.keycloakAdminPassword}' \
    | base64 -d | /root/insys/keycloak/realm-name.sh insys insys $(printf '%q' "$label")"
}

# The realm's login theme, from the values (keycloak.loginTheme, BR-168), on an existing realm too, as realm_name names it: the chart's import
# sets it only on a new realm. Nothing when the values set none. The chart has already mounted the theme from its files, or refused to render.
realm_login_theme() {
  local theme
  theme=$(sed -n 's/^keycloak:.*loginTheme: "\{0,1\}\([A-Za-z0-9._-]*\)"\{0,1\}[,}].*/\1/p' deploy/preview/values.yaml)
  [ -n "$theme" ] || return 0
  on_server "kubectl rollout status deploy/insys-keycloak --timeout=900s >/dev/null && kubectl get secret insys-secrets -o jsonpath='{.data.keycloakAdminPassword}' \
    | base64 -d | /root/insys/keycloak/realm-login-theme.sh insys $(printf '%q' "$theme")"
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
  on_server "printf '%s %s\n' '${GITHUB_RUN_ID:-local}' '$(date -u +%F)' > $openbao_run_note"
  summary "**This run's file, preview-secrets, holds the unseal key and root token of the OpenBao it initialised: the only copy.** Download it and keep it offline; every unseal needs it. GitHub deletes the artifact after 90 days."
}

# OpenBao's state, and what would explain a seal: OpenBao starts sealed whenever its container starts, so the pod's start and restarts, the
# container's last stop and its reason (OOMKilled, Error), when k3s last started, and when the server booted. Times are UTC.
openbao_state() {
  # shellcheck disable=SC2016 # it runs on the server, which expands it
  on_server 'sealed=$(/root/insys/openbao/openbao.sh sealed insys 2>/dev/null || echo unknown)
    set -- $(kubectl get pod insys-openbao-0 -o jsonpath="{.status.startTime} {.status.containerStatuses[0].restartCount} {.status.containerStatuses[0].lastState.terminated.finishedAt} {.status.containerStatuses[0].lastState.terminated.reason}")
    echo "sealed ${sealed:-unknown}; pod started ${1:-?}, restarts ${2:-?}${3:+, container last stopped $3 (${4:-no reason})}; k3s started $(systemctl show k3s -p ActiveEnterTimestamp --value); server booted $(uptime -s)"'
}

# The API's and Keycloak's port-forwards on the server, once both have rolled out, each waited for until its health or realm answers 200 (a fixed
# sleep 3 left rotate-passwords run 41929744851 with curl's empty reply, 52). If one doesn't, the pods' states and the forwards' own output say why.
forwards() {
  # shellcheck disable=SC2016 # it runs on the server, which expands it
  on_server 'kubectl rollout status deploy/insys-keycloak --timeout=600s >/dev/null && kubectl rollout status deploy/insys-api --timeout=600s >/dev/null \
      || { kubectl get pods -o wide; exit 1; }
    pkill -f "^kubectl port-forward svc/insys-" || true
    (nohup kubectl port-forward svc/insys-api 18080:8080 >/tmp/forward-api.log 2>&1 &)
    (nohup kubectl port-forward svc/insys-keycloak 18081:8080 >/tmp/forward-keycloak.log 2>&1 &)
    for url in http://127.0.0.1:18080/health/ready http://127.0.0.1:18081/realms/insys; do
      for _ in $(seq 1 90); do code=$(curl -s -o /dev/null -w "%{http_code}" "$url"); [ "$code" = 200 ] && continue 2; sleep 1; done
      echo "$url answered HTTP $code, not 200, for 90 s. The pods, then the forwards'\'' own output:"
      kubectl get pods -o wide; tail -n 10 /tmp/forward-api.log /tmp/forward-keycloak.log; exit 1
    done' || fail "a port-forward on the server didn't answer: see the pods and the forwards' output above"
}

# The realm's own emails (invitations to set a password) go through Google Workspace's SMTP relay for openfactorgroup.com (Ade, 6 November):
# it takes mail only from this server's address, over STARTTLS on 587, with no password, and only from addresses in Ade's domain. Nothing
# goes to a reserved test address (ReservedEmailAddresses, in the API), so the seed's generic accounts are sent nothing. The API that holds
# that rule must be the one running before Keycloak can send, so an older API pod can't retry a parked invitation without it.
smtp='{"host":"smtp-relay.gmail.com","port":587,"from":"insys-preview@openfactorgroup.com","fromDisplayName":"INSYS Preview","starttls":true,"ssl":false,"auth":false}'
realm_smtp() {
  on_server "kubectl rollout status deploy/insys-api --timeout=600s >/dev/null && kubectl rollout status deploy/insys-keycloak --timeout=900s >/dev/null"
  on_server "kubectl get secret insys-secrets -o jsonpath='{.data.keycloakAdminPassword}' | base64 -d | /root/insys/keycloak/realm-smtp.sh insys insys $(printf '%q' "$smtp")"
  # shellcheck disable=SC2016 # it runs on the server, which expands it
  on_server 'n=$(printf "%s" "SELECT count(*) FROM integration.parked_delivery WHERE prerequisite_code = '\''KEYCLOAK_EMAIL_UNSENT'\'' AND status IN ('\''PARKED'\'', '\''RETRYING'\'')" | kubectl exec -i insys-postgres-0 -- psql -U insys_owner -d insys -At)
    echo "Invitations still parked as KEYCLOAK_EMAIL_UNSENT: $n. Those to reserved test addresses resolve unsent at their next retry (every 15 minutes)."' || true
}

# Outbound 587 from the server to the relay, and its STARTTLS with a certificate that verifies: Keycloak's mail leaves from here.
smtp_check() {
  # shellcheck disable=SC2016 # it runs on the server, which expands it
  if on_server 'out=$(timeout 25 openssl s_client -starttls smtp -connect smtp-relay.gmail.com:587 -servername smtp-relay.gmail.com -verify_return_error -brief </dev/null 2>&1)
      printf "%s\n" "$out" | grep -E "^(CONNECTION ESTABLISHED|Protocol version|Verification|Peer certificate)" | head -n 4
      printf "%s\n" "$out" | grep -q "^Verification: OK"'; then
    echo "SMTP relay: outbound 587 from the server is open, and STARTTLS verifies."
  else
    echo "::warning::SMTP relay: outbound 587 from the server to smtp-relay.gmail.com failed or didn't verify, so Keycloak can't send its emails (see above)."
  fi
}

# Document storage, proven with the API's own keys before anyone relies on it: a short-lived pod stores an object in the documents bucket,
# reads it back and deletes it, signing as the API does (S3, SigV4) with insys-secrets' objectStoreAccessKey and objectStoreSecretKey. The
# keys reach curl as a config on stdin, never on a command line or in the log. SeaweedFS's keys had come from empty values (rotate-passwords
# run 42080719155: the first document, 500).
storage_check() {
  on_server "kubectl rollout status statefulset/insys-seaweedfs --timeout=600s >/dev/null && kubectl delete pod insys-storage-check --ignore-not-found --wait=true >/dev/null"
  on_server "kubectl apply -f - >/dev/null" <<'EOF'
apiVersion: v1
kind: Pod
metadata: {name: insys-storage-check}
spec:
  restartPolicy: Never
  containers:
    - name: check
      image: curlimages/curl:8.23.0@sha256:3fb7e13e5d179a845c97d78204f3f1064d1907037f26a3ebf91710571151fd93
      env:
        - {name: ACCESS_KEY, valueFrom: {secretKeyRef: {name: insys-secrets, key: objectStoreAccessKey}}}
        - {name: SECRET_KEY, valueFrom: {secretKeyRef: {name: insys-secrets, key: objectStoreSecretKey}}}
      command:
        - sh
        - -c
        - |
          set -eu
          obj="http://insys-seaweedfs:8333/insys-documents/preview-check/$(date +%s)"
          cfg() { printf 'user = "%s:%s"\n' "$ACCESS_KEY" "$SECRET_KEY"; }
          s3() { cfg | curl -sS --fail-with-body --retry 3 --retry-all-errors --retry-delay 5 -K - --aws-sigv4 "aws:amz:us-east-1:s3" "$@"; }
          printf 'INSYS storage check' > /tmp/body
          s3 -T /tmp/body "$obj" > /dev/null
          got=$(s3 "$obj")
          s3 -X DELETE "$obj" > /dev/null
          [ "$got" = "INSYS storage check" ] || { echo "read back something else"; exit 1; }
          echo "Document storage: stored, read back and deleted an object with the API's keys."
EOF
  # shellcheck disable=SC2016 # it runs on the server, which expands it
  on_server 'for _ in $(seq 1 60); do phase=$(kubectl get pod insys-storage-check -o jsonpath="{.status.phase}"); case "$phase" in Succeeded|Failed) break ;; esac; sleep 3; done
    kubectl logs insys-storage-check 2>&1 | tail -n 5; kubectl delete pod insys-storage-check --wait=false >/dev/null; [ "$phase" = Succeeded ]' \
    || fail "document storage refused the API's keys, or didn't answer: see above"
}

# The API makes each user's Keycloak account through the realm's client insys-user-admin, whose secret Keycloak generated on import: copied
# into insys-secrets on the server, from Keycloak to the Secret only, by deploy/keycloak/user-admin-secret.sh (it restarts the API when it
# changes). Without it, rotate-passwords run 41983797927 parked the first administrators' accounts (KEYCLOAK_UNAVAILABLE).
user_admin() {
  on_server "test -x /root/insys/keycloak/user-admin-secret.sh" || fail "the server has no keycloak/user-admin-secret.sh: run upgrade first"
  on_server "kubectl rollout status deploy/insys-keycloak --timeout=900s >/dev/null && kubectl get secret insys-secrets -o jsonpath='{.data.keycloakAdminPassword}' \
    | base64 -d | /root/insys/keycloak/user-admin-secret.sh insys insys"
}

# The seed and the accounts, through tunnels to the API and Keycloak. The accounts' passwords come back on the seed's stdout only.
# "^kubectl port-forward": anchored, so pkill -f matches only the forwards, not the remote shell running it, whose command line holds the same text.
seed() {
  local admin
  admin=$(on_server "kubectl get secret insys-secrets -o jsonpath='{.data.keycloakAdminPassword}' | base64 -d")
  mask "$admin"
  user_admin
  storage_check
  tunnel
  clients "$admin"
  dotnet build tools/Insys.Seed -c Release -v q >/dev/null
  ACCOUNTS=$(INSYS_SEED_KEYCLOAK_ADMIN_PASSWORD="$admin" dotnet tools/Insys.Seed/bin/Release/net10.0/Insys.Seed.dll --api http://127.0.0.1:18080 \
    --keycloak-url http://127.0.0.1:18081 --keycloak-admin admin --operator-tenant "$operator_tenant" --platform-1 insys-admin --platform-2 insys-admin-2 --accounts true \
    2> >(tee "$RUNNER_TEMP/seed.err" >&2)) || {
    on_server "kubectl logs deploy/insys-api --tail=400 | grep -E 'IDX[0-9]+|Bearer|metadata|SSL|certificate' | tail -n 20" || true   # why, from the API: IDX messages hide the token
    events || true
    failure_state "$(sleep 1; grep -o '"traceId":"[^"]*"' "$RUNNER_TEMP/seed.err" 2>/dev/null | tail -n 1 | cut -d'"' -f4)" || true
    fail "the seed failed"
  }
  while read -r password; do mask "$password"; done < <(jq -r '.accounts[].password' <<<"$ACCOUNTS")
  on_server "pkill -f '^kubectl port-forward svc/insys-' || true"
}

# The API and Keycloak on the runner's 18080 and 18081: port-forwards on the server, and an SSH tunnel to them, each answering 200.
tunnel() {
  local code
  forwards
  ssh -i "$key" -o UserKnownHostsFile="$RUNNER_TEMP/known_hosts" -fN -L 18080:127.0.0.1:18080 -L 18081:127.0.0.1:18081 "root@$(address)"
  for url in http://127.0.0.1:18080/health/ready http://127.0.0.1:18081/realms/insys; do   # the runner's end of the tunnel too
    for _ in $(seq 1 30); do code=$(curl -s -o /dev/null -w '%{http_code}' "$url"); [ "$code" = 200 ] && continue 2; sleep 1; done
    fail "through the runner's tunnel, $url answered HTTP $code, not 200"
  done
}

# The upgrade check (Ade, 26 November 2026): on each existing tenant, every approval type with no role holding both the authority and the function to
# decide it, from the tenant's readiness (APPROVAL_AUTHORITY), as the API now serving computes it: tools/Insys.Seed --upgrade-check, as the bootstrap's
# platform administrator, which reads only. It changes no password or other credential on any account and enables no client (Ade, 26 November 2026):
# Keycloak's administrator (the existing keycloakAdminPassword, read, never changed) impersonates that user, the back office's own client takes the
# session through its Authorization Code flow, and the check ends that session after. In upgrade it runs after chart, so the new version, which knows the new approval types, answers: by then that version is serving, so
# "the upgrade stops" means this run fails with the list, and the plan's next steps wait until it is empty (docs/plans/preview-rebuild-beta.md).
upgrade_check() {
  local admin report code=0 listed
  admin=$(on_server "kubectl get secret insys-secrets -o jsonpath='{.data.keycloakAdminPassword}' | base64 -d")
  mask "$admin"
  tunnel
  dotnet build tools/Insys.Seed -c Release -v q >/dev/null || fail "building the upgrade check (tools/Insys.Seed)"
  report=$(INSYS_SEED_KEYCLOAK_ADMIN_PASSWORD="$admin" dotnet tools/Insys.Seed/bin/Release/net10.0/Insys.Seed.dll --upgrade-check true --api http://127.0.0.1:18080 \
    --keycloak-url http://127.0.0.1:18081 --keycloak-admin admin --operator-tenant "$operator_tenant" --platform-1 insys-admin) || code=$?
  on_server "pkill -f '^kubectl port-forward svc/insys-' || true"
  printf '%s\n' "$report"
  case $code in
    0)
      summary "**Upgrade check:** clear. Every tenant checked has, for each approval type it can raise, a role holding the authority and the function to decide it."
      ;;
    1)
      listed=$(grep -E ': [A-Z0-9_]+(, [A-Z0-9_]+)*$' <<<"$report" | grep -v ': none$' || true)
      summary "**Upgrade check: not clear.** Approval types no role can decide, by tenant (enter their authority and check again; the plan's next steps wait until this is empty):" "$(printf '\n    %s' "$listed")"
      fail "the upgrade check lists approval types no role can decide: $(tr '\n' ';' <<<"$listed")"
      ;;
    *)
      fail "the upgrade check couldn't read every tenant's readiness (exit $code, see above): an unread list isn't an empty one"
      ;;
  esac
}

# Whether the API's outbox relay and its consumers are working, for a seed that failed waiting on a checker's step: per schema, events not
# yet published (with their oldest age and most attempts) and when one was last published; parked deliveries by consumer and reason; and
# the API's relay log lines. Counts and codes only, never a payload.
events() {
  local sql="SELECT 'integration' AS schema, count(*) FILTER (WHERE published_at IS NULL) AS unpublished, coalesce(max(publish_attempts) FILTER (WHERE published_at IS NULL), 0) AS attempts, coalesce(extract(epoch FROM now() - min(created_at) FILTER (WHERE published_at IS NULL))::int, 0) AS oldest_s, to_char(max(published_at), 'HH24:MI:SS') AS last_published FROM integration.outbox_event UNION ALL SELECT 'sdm' AS schema, count(*) FILTER (WHERE published_at IS NULL) AS unpublished, coalesce(max(publish_attempts) FILTER (WHERE published_at IS NULL), 0) AS attempts, coalesce(extract(epoch FROM now() - min(created_at) FILTER (WHERE published_at IS NULL))::int, 0) AS oldest_s, to_char(max(published_at), 'HH24:MI:SS') AS last_published FROM sdm.outbox_event UNION ALL SELECT 'party' AS schema, count(*) FILTER (WHERE published_at IS NULL) AS unpublished, coalesce(max(publish_attempts) FILTER (WHERE published_at IS NULL), 0) AS attempts, coalesce(extract(epoch FROM now() - min(created_at) FILTER (WHERE published_at IS NULL))::int, 0) AS oldest_s, to_char(max(published_at), 'HH24:MI:SS') AS last_published FROM party.outbox_event UNION ALL SELECT 'product' AS schema, count(*) FILTER (WHERE published_at IS NULL) AS unpublished, coalesce(max(publish_attempts) FILTER (WHERE published_at IS NULL), 0) AS attempts, coalesce(extract(epoch FROM now() - min(created_at) FILTER (WHERE published_at IS NULL))::int, 0) AS oldest_s, to_char(max(published_at), 'HH24:MI:SS') AS last_published FROM product.outbox_event UNION ALL SELECT 'rating' AS schema, count(*) FILTER (WHERE published_at IS NULL) AS unpublished, coalesce(max(publish_attempts) FILTER (WHERE published_at IS NULL), 0) AS attempts, coalesce(extract(epoch FROM now() - min(created_at) FILTER (WHERE published_at IS NULL))::int, 0) AS oldest_s, to_char(max(published_at), 'HH24:MI:SS') AS last_published FROM rating.outbox_event UNION ALL SELECT 'rules' AS schema, count(*) FILTER (WHERE published_at IS NULL) AS unpublished, coalesce(max(publish_attempts) FILTER (WHERE published_at IS NULL), 0) AS attempts, coalesce(extract(epoch FROM now() - min(created_at) FILTER (WHERE published_at IS NULL))::int, 0) AS oldest_s, to_char(max(published_at), 'HH24:MI:SS') AS last_published FROM rules.outbox_event UNION ALL SELECT 'workflow' AS schema, count(*) FILTER (WHERE published_at IS NULL) AS unpublished, coalesce(max(publish_attempts) FILTER (WHERE published_at IS NULL), 0) AS attempts, coalesce(extract(epoch FROM now() - min(created_at) FILTER (WHERE published_at IS NULL))::int, 0) AS oldest_s, to_char(max(published_at), 'HH24:MI:SS') AS last_published FROM workflow.outbox_event UNION ALL SELECT 'document' AS schema, count(*) FILTER (WHERE published_at IS NULL) AS unpublished, coalesce(max(publish_attempts) FILTER (WHERE published_at IS NULL), 0) AS attempts, coalesce(extract(epoch FROM now() - min(created_at) FILTER (WHERE published_at IS NULL))::int, 0) AS oldest_s, to_char(max(published_at), 'HH24:MI:SS') AS last_published FROM document.outbox_event UNION ALL SELECT 'billing' AS schema, count(*) FILTER (WHERE published_at IS NULL) AS unpublished, coalesce(max(publish_attempts) FILTER (WHERE published_at IS NULL), 0) AS attempts, coalesce(extract(epoch FROM now() - min(created_at) FILTER (WHERE published_at IS NULL))::int, 0) AS oldest_s, to_char(max(published_at), 'HH24:MI:SS') AS last_published FROM billing.outbox_event UNION ALL SELECT 'payment' AS schema, count(*) FILTER (WHERE published_at IS NULL) AS unpublished, coalesce(max(publish_attempts) FILTER (WHERE published_at IS NULL), 0) AS attempts, coalesce(extract(epoch FROM now() - min(created_at) FILTER (WHERE published_at IS NULL))::int, 0) AS oldest_s, to_char(max(published_at), 'HH24:MI:SS') AS last_published FROM payment.outbox_event UNION ALL SELECT 'claims' AS schema, count(*) FILTER (WHERE published_at IS NULL) AS unpublished, coalesce(max(publish_attempts) FILTER (WHERE published_at IS NULL), 0) AS attempts, coalesce(extract(epoch FROM now() - min(created_at) FILTER (WHERE published_at IS NULL))::int, 0) AS oldest_s, to_char(max(published_at), 'HH24:MI:SS') AS last_published FROM claims.outbox_event UNION ALL SELECT 'life' AS schema, count(*) FILTER (WHERE published_at IS NULL) AS unpublished, coalesce(max(publish_attempts) FILTER (WHERE published_at IS NULL), 0) AS attempts, coalesce(extract(epoch FROM now() - min(created_at) FILTER (WHERE published_at IS NULL))::int, 0) AS oldest_s, to_char(max(published_at), 'HH24:MI:SS') AS last_published FROM life.outbox_event UNION ALL SELECT 'general' AS schema, count(*) FILTER (WHERE published_at IS NULL) AS unpublished, coalesce(max(publish_attempts) FILTER (WHERE published_at IS NULL), 0) AS attempts, coalesce(extract(epoch FROM now() - min(created_at) FILTER (WHERE published_at IS NULL))::int, 0) AS oldest_s, to_char(max(published_at), 'HH24:MI:SS') AS last_published FROM general.outbox_event UNION ALL SELECT 'health' AS schema, count(*) FILTER (WHERE published_at IS NULL) AS unpublished, coalesce(max(publish_attempts) FILTER (WHERE published_at IS NULL), 0) AS attempts, coalesce(extract(epoch FROM now() - min(created_at) FILTER (WHERE published_at IS NULL))::int, 0) AS oldest_s, to_char(max(published_at), 'HH24:MI:SS') AS last_published FROM health.outbox_event UNION ALL SELECT 'finance' AS schema, count(*) FILTER (WHERE published_at IS NULL) AS unpublished, coalesce(max(publish_attempts) FILTER (WHERE published_at IS NULL), 0) AS attempts, coalesce(extract(epoch FROM now() - min(created_at) FILTER (WHERE published_at IS NULL))::int, 0) AS oldest_s, to_char(max(published_at), 'HH24:MI:SS') AS last_published FROM finance.outbox_event UNION ALL SELECT 'commission' AS schema, count(*) FILTER (WHERE published_at IS NULL) AS unpublished, coalesce(max(publish_attempts) FILTER (WHERE published_at IS NULL), 0) AS attempts, coalesce(extract(epoch FROM now() - min(created_at) FILTER (WHERE published_at IS NULL))::int, 0) AS oldest_s, to_char(max(published_at), 'HH24:MI:SS') AS last_published FROM commission.outbox_event UNION ALL SELECT 'reinsurance' AS schema, count(*) FILTER (WHERE published_at IS NULL) AS unpublished, coalesce(max(publish_attempts) FILTER (WHERE published_at IS NULL), 0) AS attempts, coalesce(extract(epoch FROM now() - min(created_at) FILTER (WHERE published_at IS NULL))::int, 0) AS oldest_s, to_char(max(published_at), 'HH24:MI:SS') AS last_published FROM reinsurance.outbox_event UNION ALL SELECT 'reporting' AS schema, count(*) FILTER (WHERE published_at IS NULL) AS unpublished, coalesce(max(publish_attempts) FILTER (WHERE published_at IS NULL), 0) AS attempts, coalesce(extract(epoch FROM now() - min(created_at) FILTER (WHERE published_at IS NULL))::int, 0) AS oldest_s, to_char(max(published_at), 'HH24:MI:SS') AS last_published FROM reporting.outbox_event UNION ALL SELECT 'iam' AS schema, count(*) FILTER (WHERE published_at IS NULL) AS unpublished, coalesce(max(publish_attempts) FILTER (WHERE published_at IS NULL), 0) AS attempts, coalesce(extract(epoch FROM now() - min(created_at) FILTER (WHERE published_at IS NULL))::int, 0) AS oldest_s, to_char(max(published_at), 'HH24:MI:SS') AS last_published FROM iam.outbox_event UNION ALL SELECT 'audit' AS schema, count(*) FILTER (WHERE published_at IS NULL) AS unpublished, coalesce(max(publish_attempts) FILTER (WHERE published_at IS NULL), 0) AS attempts, coalesce(extract(epoch FROM now() - min(created_at) FILTER (WHERE published_at IS NULL))::int, 0) AS oldest_s, to_char(max(published_at), 'HH24:MI:SS') AS last_published FROM audit.outbox_event UNION ALL SELECT 'assist' AS schema, count(*) FILTER (WHERE published_at IS NULL) AS unpublished, coalesce(max(publish_attempts) FILTER (WHERE published_at IS NULL), 0) AS attempts, coalesce(extract(epoch FROM now() - min(created_at) FILTER (WHERE published_at IS NULL))::int, 0) AS oldest_s, to_char(max(published_at), 'HH24:MI:SS') AS last_published FROM assist.outbox_event UNION ALL SELECT 'communication' AS schema, count(*) FILTER (WHERE published_at IS NULL) AS unpublished, coalesce(max(publish_attempts) FILTER (WHERE published_at IS NULL), 0) AS attempts, coalesce(extract(epoch FROM now() - min(created_at) FILTER (WHERE published_at IS NULL))::int, 0) AS oldest_s, to_char(max(published_at), 'HH24:MI:SS') AS last_published FROM communication.outbox_event ORDER BY unpublished DESC, schema"
  echo "Outbox, per schema (unpublished, most attempts, oldest in seconds, last published UTC):"
  printf '%s' "$sql" | on_server "kubectl exec -i statefulset/insys-postgres -- psql -U insys_owner -d insys -AF ' ' -t"
  echo "Parked deliveries (consumer, event type, status, prerequisite, count):"
  printf '%s' "SELECT consumer, event_type, status, coalesce(prerequisite_code, '-'), count(*) FROM integration.parked_delivery GROUP BY 1, 2, 3, 4 ORDER BY 5 DESC LIMIT 20" \
    | on_server "kubectl exec -i statefulset/insys-postgres -- psql -U insys_owner -d insys -AF ' ' -t"
  echo "The API's relay log, last 30 minutes:"
  on_server "kubectl logs deploy/insys-api --since=30m | grep -E 'Outbox|dead-letter|[Pp]arked|relay pass' | tail -n 20"
}

# For a seed that failed on the API's own fault (a 500): the API's line for the failed request's trace (the exception's types, a database
# error's SQLSTATE and constraint, a client library's error code; never a message), else its last few 500s; and the stores a request
# leans on: SeaweedFS (its pod, whether its S3 config has an empty key, its buckets) and OpenBao (sealed or not). No key or secret is printed.
failure_state() { # trace id, if the seed's error gave one
  local trace=${1:-}
  echo "The API's unhandled errors${trace:+ for trace $trace} (types and codes only):"
  # shellcheck disable=SC2016 # it runs on the server, which expands it
  on_server 'lines=$(kubectl logs deploy/insys-api --since=90m | grep -F "Unhandled " | grep -F -- "'"${trace:-Unhandled}"'" | tail -n 5)
    [ -n "$lines" ] || lines=$(kubectl logs deploy/insys-api --since=90m | grep -F "Unhandled " | tail -n 5)
    printf "%s\n" "${lines:-none in the last 90 minutes}"' || true
  echo "SeaweedFS (pod, restarts; S3 identities with an empty key; buckets):"
  # shellcheck disable=SC2016 # it runs on the server, which expands it
  on_server 'kubectl get pod insys-seaweedfs-0 -o jsonpath="{.status.phase} {.status.containerStatuses[0].restartCount}{\"\n\"}"
    kubectl exec insys-seaweedfs-0 -c seaweedfs -- sh -c "grep -o \"accessKey\\\": \\\"\\\"\" /etc/seaweedfs/s3.json | wc -l"
    printf "s3.bucket.list\n" | kubectl exec -i insys-seaweedfs-0 -c seaweedfs -- weed shell 2>/dev/null | grep -E "size|bucket" | head -n 5' || true
  echo "OpenBao: $(openbao_state || true)"
}

# Each app's Keycloak client gets the Preview's addresses, through the tunnel: sign-in returns to the app, and the browser may call the token endpoint.
clients() {
  local answer token id base
  # The password on curl's stdin and the token in a header from a process substitution, never on a command line. Each call says which it was,
  # with curl's own exit code (52: an empty reply, as in rotate-passwords run 41929744851).
  answer=$(printf '%s' "$1" | curl -sf -d grant_type=password -d client_id=admin-cli -d username=admin --data-urlencode password@- \
    http://127.0.0.1:18081/realms/master/protocol/openid-connect/token) || fail "Keycloak's admin token, through the tunnel to 18081: curl exit $?"
  token=$(jq -er .access_token <<<"$answer") || fail "Keycloak's admin token: its answer holds no access_token"
  mask "$token"
  for client in insys-web:/ broker-portal:/broker/ member-portal:/member/ provider-portal:/provider/; do
    base="https://$PREVIEW_DOMAIN${client#*:}"
    answer=$(curl -sf -H @<(printf 'Authorization: Bearer %s\n' "$token") "http://127.0.0.1:18081/admin/realms/insys/clients?clientId=${client%%:*}") \
      || fail "finding Keycloak's client ${client%%:*}: curl exit $?"
    id=$(jq -er '.[0].id' <<<"$answer") || fail "Keycloak has no client ${client%%:*}"
    answer=$(curl -sf -H @<(printf 'Authorization: Bearer %s\n' "$token") "http://127.0.0.1:18081/admin/realms/insys/clients/$id") \
      || fail "reading Keycloak's client ${client%%:*}: curl exit $?"
    jq --arg base "$base" --arg origin "https://$PREVIEW_DOMAIN" '.redirectUris = [$base + "*"] | .webOrigins = [$origin] | .rootUrl = $base | .attributes["post.logout.redirect.uris"] = ($base + "*")' <<<"$answer" \
      | curl -sf -X PUT -H @<(printf 'Authorization: Bearer %s\n' "$token") -H 'Content-Type: application/json' --data-binary @- "http://127.0.0.1:18081/admin/realms/insys/clients/$id" \
      || fail "updating Keycloak's client ${client%%:*}: exit $?"
  done
}

# Encrypted to Ade's key, the only form that leaves memory. Written once OpenBao is initialised too, so a later failure keeps its keys.
encrypt() {
  : "${PREVIEW_AGE_RECIPIENT:?}"
  jq -n --argjson accounts "${ACCOUNTS:-null}" --argjson openbao "${OPENBAO_INIT:-null}" --arg domain "$PREVIEW_DOMAIN" \
    '{domain: $domain, accounts: $accounts.accounts, openbao: ($openbao | if . then {unsealKey: .keys_base64[0], rootToken: .root_token} else null end)}' \
    | age -r "$PREVIEW_AGE_RECIPIENT" -o "$out"
  echo "Encrypted for Ade: $(basename "$out")."
  if [ -z "${OPENBAO_INIT:-}" ]; then   # only provision and reset initialise OpenBao, so only their files hold its key
    local run
    run=$(on_server "cat $openbao_run_note 2>/dev/null" || true)
    echo "::warning::This run's file has no OpenBao unseal key. It is in $(openbao_file "$run")."
    summary "**This run's file has no OpenBao unseal key.** It is in $(openbao_file "$run"). Keep that file: without it a sealed OpenBao can't be unsealed."
  fi
}
openbao_file() { # the note's "<run id> <date>"
  if [ -n "$1" ]; then echo "the file of run ${1%% *}, which initialised this OpenBao on ${1#* }"; else echo "the file of the run that last initialised OpenBao, which the server has no record of (before 14 November a run didn't note it)"; fi
}

# TLS is proven before anyone relies on it: the certificate must come from the CA asked for. Staging's issuers name Let's Encrypt too
# ("O = Let's Encrypt, CN = (STAGING) ..."), so production is Let's Encrypt without "(STAGING)", and staging is Let's Encrypt with it.
issued_by() { # issuer, then staging or production
  case "$1" in *"O = Let's Encrypt"*) ;; *) return 1 ;; esac
  case "$1" in *"(STAGING)"*) [ "$2" = staging ] ;; *) [ "$2" = production ] ;; esac
}
check_tls() {
  local want=${PREVIEW_ACME:-staging}
  for host in "$PREVIEW_DOMAIN" "id.$PREVIEW_DOMAIN"; do
    for _ in $(seq 1 60); do
      issuer=$(echo | openssl s_client -connect "$(address):443" -servername "$host" 2>/dev/null | openssl x509 -noout -issuer 2>/dev/null || true)
      issued_by "$issuer" "$want" && break
      sleep 10
    done
    echo "$host: $issuer"
    issued_by "$issuer" "$want" || {
      on_server "kubectl -n kube-system logs deploy/traefik --tail=300 | grep -iE 'acme|certificate|resolver' | tail -n 20" || true   # why, from Traefik: no secret in it
      fail "$host has no Let's Encrypt $want certificate yet"
    }
  done
}

# Backups and the restore rehearsal (docs/preview/restore.md). The nightly job (deploy/preview/backup.yaml) writes the databases' dumps and
# OpenBao's volume to /var/backups/insys on the server. Nothing here prints a secret: counts, names, sizes and pass or fail only.
counts() { # rows in a few tables, to compare before and after a restore
  printf '%s' "SELECT 'parties ' || (SELECT count(*) FROM party.party) || ', users ' || (SELECT count(*) FROM iam.user_account)
    || ', documents ' || (SELECT count(*) FROM document.document) || ', General policies ' || (SELECT count(*) FROM general.policy)
    || ', Life policies ' || (SELECT count(*) FROM life.policy) || ', Health memberships ' || (SELECT count(*) FROM health.membership)
    || ', approvals ' || (SELECT count(*) FROM workflow.approval) || ', data keys ' || (SELECT count(*) FROM integration.field_data_key)" \
    | on_server "kubectl exec -i insys-postgres-0 -- psql -U insys_owner -d insys -At"
}

# read-authority (Ade approved one read-only run, 26 November 2026): OFTZA's approval authority for STATIC_DATA_T1, read only. It prints, for the
# tenant OFTZA only: (a) each workflow.cfg_delegation_and_approval_authority row for STATIC_DATA_T1 (role, limit, currency, status, dates);
# (b) whether DEMO_CHECKER holds authority in force today for each of the five approval types new since ca1bf2b and for STATIC_DATA_T1;
# (c) the roles assigned to demouser01 and demouser02 (assignment status and dates), and whether each role holds, in force today, the
# delegation table's functions: SD_WORKFLOW_AND_CASE_MANAGEMENT_VIEW, _MAINTAIN (enter, submit, activate: static_data.create and .update) and
# _APPROVE (static_data.approve; the table's catalogue module is Workflow and Case Management, StaticDataFunctions). Codes, counts, dates and
# yes/no only: no name, email or other personal data. The session can't write: default_transaction_read_only at connection (PGOPTIONS), again
# by SET, and BEGIN READ ONLY ... ROLLBACK. In force: record_status PUBLISHED or EFFECTIVE (the configuration lifecycle), effective today
# (the database's current_date, UTC on the Preview, not the tenant's business date).
read_authority_sql() {
  cat <<'SQL'
SET default_transaction_read_only = on;
BEGIN READ ONLY;
SELECT 'tenant OFTZA: ' || CASE WHEN set_config('app.tenant_id', coalesce((SELECT id::text FROM tenant.tenant WHERE tenant_code = 'OFTZA'), '00000000-0000-0000-0000-000000000000'), true) <> '00000000-0000-0000-0000-000000000000' THEN 'found' ELSE 'not found' END;
SELECT 'transaction read only: ' || current_setting('transaction_read_only');
SELECT '(a) STATIC_DATA_T1 authority rows: ' || count(*) FROM workflow.cfg_delegation_and_approval_authority a WHERE a.tenant_id = (SELECT id FROM tenant.tenant WHERE tenant_code = 'OFTZA') AND a.transaction_type = 'STATIC_DATA_T1';
SELECT '(a) ' || a.role_code || ' limit ' || a.limit_value || ' ' || coalesce(a.currency, 'base') || ' ' || a.record_status || ' v' || a.version || ' from ' || a.effective_from || ' to ' || coalesce(a.effective_to::text, 'open') FROM workflow.cfg_delegation_and_approval_authority a WHERE a.tenant_id = (SELECT id FROM tenant.tenant WHERE tenant_code = 'OFTZA') AND a.transaction_type = 'STATIC_DATA_T1' ORDER BY a.role_code, a.effective_from, a.version;
SELECT '(b) DEMO_CHECKER holds authority in force today for ' || x.ty || ': ' || CASE WHEN EXISTS (SELECT 1 FROM workflow.cfg_delegation_and_approval_authority a WHERE a.tenant_id = (SELECT id FROM tenant.tenant WHERE tenant_code = 'OFTZA') AND a.role_code = 'DEMO_CHECKER' AND a.transaction_type = x.ty AND a.record_status IN ('PUBLISHED', 'EFFECTIVE') AND a.effective_from <= current_date AND (a.effective_to IS NULL OR a.effective_to > current_date)) THEN 'yes' ELSE 'no' END FROM unnest(ARRAY['COMMISSION_CLAWBACK_INVOICE', 'CREDIT_NOTE', 'DEAD_LETTER_DISCARD', 'DISCLOSURE_SETTING_CHANGE', 'PAYOUT_RETRY', 'STATIC_DATA_T1']) WITH ORDINALITY AS x(ty, n) ORDER BY x.n;
SELECT '(c) users found: ' || count(*) || ' of 2' FROM iam.user_account u WHERE u.tenant_id = (SELECT id FROM tenant.tenant WHERE tenant_code = 'OFTZA') AND lower(u.username) IN ('demouser01', 'demouser02', 'oftza.demouser01', 'oftza.demouser02');
SELECT '(c) ' || lower(u.username) || ' ' || u.status || ': ' || coalesce(r.role_code || ' ' || r.status || ' from ' || r.valid_from || ' to ' || coalesce(r.valid_to::text, 'open'), 'no role assignment') FROM iam.user_account u LEFT JOIN iam.user_role_assignment r ON r.tenant_id = u.tenant_id AND r.user_id = u.id WHERE u.tenant_id = (SELECT id FROM tenant.tenant WHERE tenant_code = 'OFTZA') AND lower(u.username) IN ('demouser01', 'demouser02', 'oftza.demouser01', 'oftza.demouser02') ORDER BY 1;
SELECT '(c) role ' || ro.role_code || ' holds ' || f.fn || ' in force today: ' || CASE WHEN EXISTS (SELECT 1 FROM iam.cfg_role_function_assignments g WHERE g.tenant_id = (SELECT id FROM tenant.tenant WHERE tenant_code = 'OFTZA') AND g.role_code = ro.role_code AND g.function_code = f.fn AND g.record_status IN ('PUBLISHED', 'EFFECTIVE') AND g.effective_from <= current_date AND (g.effective_to IS NULL OR g.effective_to > current_date)) THEN 'yes' ELSE 'no' END FROM (SELECT DISTINCT r.role_code FROM iam.user_role_assignment r JOIN iam.user_account u ON u.tenant_id = r.tenant_id AND u.id = r.user_id WHERE u.tenant_id = (SELECT id FROM tenant.tenant WHERE tenant_code = 'OFTZA') AND lower(u.username) IN ('demouser01', 'demouser02', 'oftza.demouser01', 'oftza.demouser02')) ro CROSS JOIN (VALUES (1, 'SD_WORKFLOW_AND_CASE_MANAGEMENT_VIEW'), (2, 'SD_WORKFLOW_AND_CASE_MANAGEMENT_MAINTAIN'), (3, 'SD_WORKFLOW_AND_CASE_MANAGEMENT_APPROVE')) AS f(n, fn) ORDER BY ro.role_code, f.n;
ROLLBACK;
SQL
}

read_authority() {
  echo "OFTZA's approval authority, read only (codes, counts, dates and yes/no):"
  read_authority_sql | on_server "kubectl exec -i insys-postgres-0 -- env PGOPTIONS='-c default_transaction_read_only=on' psql -U insys_owner -d insys -At -v ON_ERROR_STOP=1" \
    || fail "reading OFTZA's approval authority (read only; nothing was changed)"
}

# OpenBao unsealed, and a data key the database holds unwrapped by Transit with the API's own token: so what was encrypted before reads
# after. The key's plaintext is never printed; its ciphertext is useless without OpenBao.
kms_check() {
  local state wrapped
  state=$(openbao_state)
  echo "OpenBao: $state"
  [[ $state == "sealed false;"* ]] || fail "OpenBao is sealed: unseal it (the handover's Unsealing OpenBao), then run this again"
  wrapped=$(printf '%s' "SELECT convert_from(ciphertext, 'UTF8') FROM integration.field_data_key WHERE master_key_ref LIKE 'openbao:%' ORDER BY created_at LIMIT 1" \
    | on_server "kubectl exec -i insys-postgres-0 -- psql -U insys_owner -d insys -At")
  [ -n "$wrapped" ] || { echo "OpenBao: unsealed; no data key stored yet to decrypt."; return 0; }
  on_server "kubectl delete pod insys-kms-check --ignore-not-found --wait=true >/dev/null"
  jq -n --arg c "$wrapped" '{apiVersion: "v1", kind: "Pod", metadata: {name: "insys-kms-check"}, spec: {restartPolicy: "Never", containers: [{name: "check",
      image: "curlimages/curl:8.23.0@sha256:3fb7e13e5d179a845c97d78204f3f1064d1907037f26a3ebf91710571151fd93",
      env: [{name: "KMS_TOKEN", valueFrom: {secretKeyRef: {name: "insys-secrets", key: "kmsToken"}}}, {name: "WRAPPED", value: $c}],
      command: ["sh", "-c", "set -eu; printf \"header = \\\"X-Vault-Token: %s\\\"\\n\" \"$KMS_TOKEN\" | curl -sS --fail-with-body -K - -o /dev/null -X POST --data \"{\\\"ciphertext\\\":\\\"$WRAPPED\\\"}\" http://insys-openbao:8200/v1/transit/decrypt/insys && echo \"OpenBao: unsealed, and a stored data key decrypts with the API token.\""]}]}}' \
    | on_server "kubectl apply -f - >/dev/null"
  # shellcheck disable=SC2016 # it runs on the server, which expands it
  on_server 'for _ in $(seq 1 40); do phase=$(kubectl get pod insys-kms-check -o jsonpath="{.status.phase}"); case "$phase" in Succeeded|Failed) break ;; esac; sleep 3; done
    kubectl logs insys-kms-check 2>&1 | tail -n 3; kubectl delete pod insys-kms-check --wait=false >/dev/null; [ "$phase" = Succeeded ]' \
    || fail "OpenBao didn't decrypt a stored data key with the API's token: see above"
}

# A backup now, by the nightly job, then each file checked: the dumps list their contents (pg_restore --list), the archive opens (tar -t).
# The pod takes the whole dump before listing it: pg_restore --list reads only the table of contents and exits, and kubectl exec -i, still
# forwarding the rest of the file, hung until SSH dropped (backup run 42299623098: about 17 minutes per dump, and no count, unnoticed).
backup_now() {
  local day items
  day=$(date -u +%F)
  # shellcheck disable=SC2016 # it runs on the server, which expands it
  # The job's last step checks OpenBao's seal: a seal fails the job after the backups were taken, which kms_check below reports in turn.
  on_server 'job=insys-backup-$(date -u +%H%M%S); kubectl create job "$job" --from=cronjob/insys-backup >/dev/null || exit 1
    for _ in $(seq 1 300); do   # until the Job ends: Complete, or Failed once its retries are spent (a seal has none)
      ended=$(kubectl get job "$job" -o jsonpath="{range .status.conditions[?(@.status==\"True\")]}{.type} {end}")
      case "$ended" in *Complete*) exit 0 ;; *Failed*) break ;; esac; sleep 3
    done
    log=$(kubectl logs "job/$job" -c seal 2>/dev/null); echo "$log"
    case "$log" in *"OpenBao is sealed"*) exit 0 ;; esac; exit 1' \
    || fail "the backup job didn't complete"
  echo "Backup of $day, on the server in /var/backups/insys:"
  on_server "ls -l /var/backups/insys | awk '/$day/ {print \"  \" \$NF \", \" \$5 \" bytes\"}'"
  for db in insys keycloak; do
    items=$(on_server "timeout 600 kubectl exec -i insys-postgres-0 -- sh -c 'cat > /tmp/list.dump && pg_restore --list /tmp/list.dump | grep -vc \"^;\"; rm -f /tmp/list.dump' \
      < /var/backups/insys/$day-$db.dump") || true
    [[ $items =~ ^[1-9][0-9]*$ ]] || fail "$day-$db.dump didn't list as a pg_dump archive (got: ${items:-nothing})"
    echo "  $day-$db.dump lists $items items"
  done
  echo "  $day-openbao.tar.gz holds $(on_server "tar -tzf /var/backups/insys/$day-openbao.tar.gz | wc -l") entries"
  echo "Counts now: $(counts)"
  storage_check
  kms_check
}

# Restores the nightly backup of PREVIEW_BACKUP_DAY (yyyy-mm-dd): both databases, then OpenBao's volume. OpenBao comes back sealed, so SSH
# is left open from Ade's address for him to unseal it with the key of the OpenBao the backup came from; then backup_now checks it all.
restore() {
  local day=${PREVIEW_BACKUP_DAY:-}
  [[ $day =~ ^[0-9]{4}-[0-9]{2}-[0-9]{2}$ ]] || fail "set backup_day to the backup's date, yyyy-mm-dd"
  echo "Before the restore: $(counts)"
  on_server "DAY='$day' bash -s" <<'EOF'
set -euo pipefail
b=/var/backups/insys
for f in "$b/$DAY-insys.dump" "$b/$DAY-keycloak.dump" "$b/$DAY-openbao.tar.gz"; do [ -s "$f" ] || { echo "There's no $f."; exit 1; }; done
kubectl scale deploy/insys-api deploy/insys-keycloak --replicas=0 >/dev/null
kubectl wait --for=delete pod -l 'app.kubernetes.io/name in (api, keycloak)' --timeout=300s >/dev/null 2>&1 || true
for db in insys keycloak; do
  # Dropped and made again from the dump (--create brings its settings and grants), not --clean: --clean can't drop a partition's
  # inherited constraints, and left 240 errors on a seeded INSYS database in rehearsal; drop-and-create left none, with the same grants
  # and row-level security policies.
  kubectl exec insys-postgres-0 -- psql -U insys_owner -d postgres -qc "DROP DATABASE IF EXISTS $db WITH (FORCE)"
  if ! kubectl exec -i insys-postgres-0 -- pg_restore -U insys_owner -d postgres --create < "$b/$DAY-$db.dump" 2> "/tmp/restore-$db.err"; then
    echo "$db: pg_restore reported $(grep -c 'error:' "/tmp/restore-$db.err") error(s); the first: $(grep -m1 'error:' "/tmp/restore-$db.err" | cut -c1-200)"
    exit 1
  fi
  echo "$db: restored from $DAY."
done
kubectl scale statefulset/insys-openbao --replicas=0 >/dev/null
kubectl wait --for=delete pod/insys-openbao-0 --timeout=300s >/dev/null 2>&1 || true
kubectl delete pod insys-openbao-restore --ignore-not-found --wait=true >/dev/null
kubectl apply -f - >/dev/null <<POD
apiVersion: v1
kind: Pod
metadata: {name: insys-openbao-restore}
spec:
  restartPolicy: Never
  containers:
    - name: restore
      image: busybox:1.37
      command: [sh, -c, 'set -e; find /openbao -mindepth 1 -delete; tar -C /openbao -xzf /backup/$DAY-openbao.tar.gz; echo "OpenBao: its volume restored from $DAY."']
      volumeMounts: [{name: openbao, mountPath: /openbao}, {name: backup, mountPath: /backup, readOnly: true}]
  volumes:
    - {name: openbao, persistentVolumeClaim: {claimName: data-insys-openbao-0}}
    - {name: backup, hostPath: {path: /var/backups/insys, type: Directory}}
POD
for _ in $(seq 1 60); do phase=$(kubectl get pod insys-openbao-restore -o jsonpath='{.status.phase}'); case "$phase" in Succeeded|Failed) break ;; esac; sleep 3; done
kubectl logs insys-openbao-restore | tail -n 2; kubectl delete pod insys-openbao-restore --wait=false >/dev/null
[ "$phase" = Succeeded ] || { echo "OpenBao's volume wasn't restored."; exit 1; }
kubectl scale statefulset/insys-openbao --replicas=1 >/dev/null && kubectl rollout status statefulset/insys-openbao --timeout=600s >/dev/null
kubectl scale deploy/insys-keycloak deploy/insys-api --replicas=1 >/dev/null
kubectl rollout status deploy/insys-keycloak --timeout=900s >/dev/null
EOF
  echo "After the restore: $(counts)"
}

# Where the nightly backup's seal alert goes: PREVIEW_ALERT_TO (a secret Ade sets), with the realm's relay and sender. On the server only, from
# stdin; the address is never on a command line or in the log. Unset, the job still fails on a seal, and the run summary says no email goes.
alert_secret() {
  jq -nc --arg to "${PREVIEW_ALERT_TO:-}" --argjson smtp "$smtp" --arg helo "$PREVIEW_DOMAIN" \
    '{apiVersion: "v1", kind: "Secret", metadata: {name: "insys-alert"}, type: "Opaque",
      stringData: {to: $to, from: $smtp.from, relay: "\($smtp.host):\($smtp.port)", helo: $helo}}' \
    | on_server "kubectl apply -f - >/dev/null"
  alert_note
}
# The run summary's word on the seal alert. The address reaches the server only through alert_secret, in provision, upgrade and reset.
alert_note() {
  if [ -n "${PREVIEW_ALERT_TO:-}" ]; then
    summary "**Seal alert:** PREVIEW_ALERT_TO is set. A change to it takes effect on the server at the next provision, upgrade or reset."
  else
    summary "**No seal alert address:** PREVIEW_ALERT_TO isn't set, so a seal fails the nightly backup on the server only, and check shows it. Once set, it takes effect at the next provision, upgrade or reset."
  fi
}

# Security updates stay automatic; restarts and reboots don't (Ade, 14 November). unattended-upgrades and apt's daily timers stay on, with
# Ubuntu's own settings (security updates only); automatic reboot is false, and needrestart lists and restarts nothing, k3s included. check
# reports a pending reboot and what needrestart lists, so Ade can plan one. os-update applies everything else. k3s's own unit still restarts
# it if it crashes (Restart=always), which keeps its containers, OpenBao's included, running.
os_policy() {
  # shellcheck disable=SC2016 # it runs on the server, which expands it
  on_server 'set -e
    rm -f /etc/apt/apt.conf.d/99insys-no-automatic
    cat > /etc/apt/apt.conf.d/99insys-no-automatic-reboot <<EOF
// INSYS Preview (Ade, 14 November 2026): security updates apply themselves; the server never reboots itself for them.
APT::Periodic::Update-Package-Lists "1";
APT::Periodic::Unattended-Upgrade "1";
Unattended-Upgrade::Automatic-Reboot "false";
EOF
    mkdir -p /etc/needrestart/conf.d
    cat > /etc/needrestart/conf.d/99insys.conf <<EOF
# INSYS Preview (Ade, 14 November 2026): list what needs a restart, restart nothing, k3s included.
\$nrconf{restart} = "l";
\$nrconf{override_rc}{qr(^k3s)} = 0;
EOF
    systemctl enable --now apt-daily.timer apt-daily-upgrade.timer >/dev/null 2>&1 || true
    echo "Server policy: unattended security updates $(apt-config dump | sed -n "s/^APT::Periodic::Unattended-Upgrade \"\(.*\)\";/\1/p"), automatic reboot $(apt-config dump | sed -n "s/^Unattended-Upgrade::Automatic-Reboot \"\(.*\)\";/\1/p"), apt timers $(systemctl is-enabled apt-daily.timer apt-daily-upgrade.timer 2>/dev/null | sort -u | paste -sd/ -), needrestart lists only."'
}

# What would need a restart or a reboot, for Ade to plan one: /var/run/reboot-required (and the packages that set it), a newer kernel waiting,
# and the services needrestart lists. Nothing is restarted.
pending_restarts() {
  # shellcheck disable=SC2016 # it runs on the server, which expands it
  on_server 'if [ -f /var/run/reboot-required ]; then printf "reboot pending (%s)" "$(paste -sd, /var/run/reboot-required.pkgs 2>/dev/null)"; else printf "no reboot pending"; fi
    nr=$(NEEDRESTART_MODE=l needrestart -b -r l 2>/dev/null)
    case "$nr" in *"NEEDRESTART-KSTA: "[23]*) printf "; a newer kernel is installed" ;; esac
    svc=$(printf "%s\n" "$nr" | sed -n "s/^NEEDRESTART-SVC: //p" | paste -sd, -)
    printf "; needrestart lists: %s\n" "${svc:-nothing}"'
}

# The server's packages, all of them (security updates already apply themselves), with nothing restarted or rebooted. A reboot restarts
# OpenBao, which comes back sealed: that is Ade's call, with the unseal key at hand. k3s isn't an apt package, so this doesn't change it.
os_update() {
  # shellcheck disable=SC2016 # it runs on the server, which expands it
  on_server 'set -e; export DEBIAN_FRONTEND=noninteractive NEEDRESTART_MODE=l NEEDRESTART_SUSPEND=1
    apt-get update -qq
    before=$(dpkg-query -W -f "\${Package} \${Version}\n" | sort)
    apt-get -y -qq -o Dpkg::Options::=--force-confold upgrade >/dev/null
    after=$(dpkg-query -W -f "\${Package} \${Version}\n" | sort)
    changed=$(comm -13 <(echo "$before") <(echo "$after"))
    echo "Packages updated: $(printf "%s" "$changed" | grep -c . || true)"; printf "%s\n" "$changed" | sed -n "1,80s/^/  /p"
    needrestart -b -r l 2>/dev/null | sed -n "s/^NEEDRESTART-SVC: /Needs a restart (not done): /p; s/^NEEDRESTART-KSTA: 1$/Kernel: current/p; s/^NEEDRESTART-KSTA: [23]$/Kernel: a newer one is installed, used after a reboot/p"
    if [ -f /var/run/reboot-required ]; then echo "A reboot is needed ($(paste -sd, /var/run/reboot-required.pkgs 2>/dev/null)). Not done: a reboot seals OpenBao."; else echo "No reboot is needed."; fi'
}

# The rebuild plan's 0A.2 (Ade, 20 November 2026): the pending reboot, only through this workflow's approved preview environment. It refuses unless
# today's backup is on the server (0A.1 first), reboots with systemctl (a clean shutdown, recorded in the journal with this run's id), waits for a new
# boot, then checks it: the boot time and kernel, what still waits for a reboot, the node, and every pod's state. OpenBao comes back sealed, as after
# any reboot; that is reported, not a failure, as the reset replaces it. Nothing else is changed.
reboot_server() {
  local day before kernel after
  day=$(date -u +%F)
  on_server "for f in $day-insys.dump $day-keycloak.dump $day-openbao.tar.gz; do [ -s /var/backups/insys/\$f ] || exit 1; done" \
    || fail "no backup of $day on the server: run backup first (the rebuild plan's 0A.1), then reboot"
  before=$(on_server 'uptime -s'); kernel=$(on_server 'uname -r')
  echo "Before: booted $before, kernel $kernel; $(pending_restarts)"
  on_server "logger -t insys-preview 'reboot by preview.sh, run ${GITHUB_RUN_ID:-unknown}'; systemd-run --on-active=5 --unit=insys-preview-reboot systemctl reboot >/dev/null" \
    || fail "the reboot couldn't be scheduled"
  echo "Reboot scheduled; waiting for the server to come back."
  for _ in $(seq 1 120); do   # up to 10 minutes for a new boot
    sleep 5
    after=$(on_server 'uptime -s' 2>/dev/null) || continue
    [ "$after" != "$before" ] && break
  done
  [ -n "${after:-}" ] && [ "$after" != "$before" ] || fail "the server didn't come back with a new boot within 10 minutes (last boot seen: ${after:-none})"
  echo "After: booted $after, kernel $(on_server 'uname -r') (was $kernel); $(pending_restarts)"
  # shellcheck disable=SC2016 # it runs on the server, which expands it
  on_server 'kubectl wait --for=condition=Ready node --all --timeout=600s >/dev/null' || fail "the k3s node wasn't Ready within 10 minutes of the boot"
  # Every pod Running and ready, or Succeeded; OpenBao is left out of the wait, as it comes back sealed and so not ready.
  # shellcheck disable=SC2016 # it runs on the server, which expands it
  on_server 'for _ in $(seq 1 120); do
      waiting=$(kubectl get pods -A --no-headers -o custom-columns="NS:.metadata.namespace,POD:.metadata.name,PHASE:.status.phase,READY:.status.containerStatuses[*].ready" \
        | awk "\$2 !~ /openbao/ && \$3 != \"Succeeded\" && (\$3 != \"Running\" || \$4 ~ /false/)")
      [ -z "$waiting" ] && exit 0; sleep 5
    done
    printf "Not back within 10 minutes:\n%s\n" "$waiting"; exit 1' || fail "pods didn't come back after the reboot"
  echo "k3s: the node is Ready and every pod is back."
  on_server 'kubectl get pods -A --no-headers -o custom-columns="POD:.metadata.name,PHASE:.status.phase,RESTARTS:.status.containerStatuses[*].restartCount"' | sed 's/^/  /'
  echo "OpenBao: $(openbao_state || echo "didn't answer") (sealed is expected after a reboot; the reset replaces it)."
  summary "Reboot: booted $before, now $after; kernel $kernel, now $(on_server 'uname -r'). Node Ready, every pod back. OpenBao: $(openbao_state || echo "didn't answer")."
}

# Read only: what rebooted the server or restarted k3s. The boots, the reboot records, unattended-upgrades' and apt's logs for the last 14
# days, needrestart's lines in apt's log, and the journal's system lines around each boot and k3s's last start; then Hetzner's own record
# of actions on the server (a reboot or reset from its API or console is there). The repository is public, so only system lines are read:
# systemd, logind, the kernel, the package tools and k3s's start and stop, never an application's log, a session's address or a command line.
diagnose() {
  local id
  # shellcheck disable=SC2016 # it runs on the server, which expands it
  on_server 'since=$(date -u -d "-14 days" +%F)
    echo "== Now: $(date -u "+%F %T") UTC; booted $(uptime -s); k3s started $(systemctl show k3s -p ActiveEnterTimestamp --value)"
    echo "== Journal storage: $( [ -d /var/log/journal ] && echo persistent || echo "volatile, so earlier boots are not kept")"
    echo "== Boots"; journalctl --list-boots --no-pager 2>/dev/null | tail -n 12
    echo "== Reboots and shutdowns (wtmp)"; last -x -F reboot shutdown 2>/dev/null | grep -v "^$" | head -n 12
    echo "== Automatic updates as set now"
    apt-config dump | grep -E "^(APT::Periodic::(Update-Package-Lists|Unattended-Upgrade)|Unattended-Upgrade::Automatic-Reboot(-Time)?) "
    systemctl list-timers apt-daily.timer apt-daily-upgrade.timer --all --no-pager 2>/dev/null | sed -n "1,4p"
    grep -hE "^[[:space:]]*\\\$nrconf\{(restart|kernelhints)\}" /etc/needrestart/needrestart.conf /etc/needrestart/conf.d/*.conf 2>/dev/null | sed "s/^/needrestart: /"
    echo "== unattended-upgrades since $since"
    zcat -f /var/log/unattended-upgrades/unattended-upgrades.log* 2>/dev/null | awk -v s="$since" "substr(\$0,1,10) >= s" | grep -vE " DEBUG " | sort | tail -n 60 | cut -c1-300
    echo "== apt runs since $since"
    zcat -f /var/log/apt/history.log* 2>/dev/null | awk -v s="$since" "/^Start-Date: /{p = (\$2 >= s)} p && /^(Start-Date|Commandline|Requested-By|End-Date):/" | cut -c1-200 | tail -n 60
    echo "== needrestart and reboot lines in the apt term log since $since"
    zcat -f /var/log/apt/term.log* 2>/dev/null | awk -v s="$since" "/^Log started: /{p = (\$3 >= s)} p && (/^Log started/ || tolower(\$0) ~ /restart|reboot|k3s|kernel/)" | cut -c1-200 | tail -n 60
    window() { # from, to, title: the system lines of the journal, no sessions or addresses
      echo "== Journal, $3 ($1 to $2 UTC)"
      journalctl --no-pager --utc -o short-iso --since "$1" --until "$2" -t systemd -t systemd-logind -t systemd-shutdown -t kernel -t unattended-upgrade \
        -t unattended-upgrades-shutdown -t needrestart -t apt.systemd.daily -t cloud-init 2>/dev/null \
        | grep -vE "session|Session|user@|from [0-9a-f:.]+|ssh" | tail -n 50 | cut -c1-220
      journalctl --no-pager --utc -o short-iso --since "$1" --until "$2" -u k3s 2>/dev/null \
        | grep -E "k3s.service|Stopping|Stopped|Starting|Started|signal|shutting down" | grep -vE "msg=|level=" | tail -n 15 | cut -c1-220
    }
    for boot in $(journalctl --list-boots --no-pager -o json 2>/dev/null | jq -r ".[-5:][] | .first_entry" 2>/dev/null); do
      t=$(date -u -d "@$((boot / 1000000))" "+%F %T")
      window "$(date -u -d "$t 25 minutes ago" "+%F %T")" "$(date -u -d "$t 2 minutes" "+%F %T")" "before and at the boot of $t"
    done
    k=$(date -u -d "$(systemctl show k3s -p ActiveEnterTimestamp --value)" "+%F %T")
    window "$(date -u -d "$k 25 minutes ago" "+%F %T")" "$(date -u -d "$k 2 minutes" "+%F %T")" "before and at the last start of k3s, $k"'
  echo "== Hetzner's actions on the server (newest first)"
  id=$(hcloud server describe "$name" -o format='{{.ID}}')
  printf 'header = "Authorization: Bearer %s"\n' "$HCLOUD_TOKEN" \
    | curl -sS --fail-with-body -K - "https://api.hetzner.cloud/v1/servers/$id/actions?sort=started:desc&per_page=25" \
    | jq -r '.actions[] | "\(.started)  \(.command)  \(.status)"'
}

# A connection with one given key only, nothing else offered: for rotate-ssh-key, to prove which key the server accepts.
ssh_with() { # key file, then the command
  local with=$1; shift
  ssh -i "$with" -o IdentitiesOnly=yes -o BatchMode=yes -o PreferredAuthentications=publickey -o StrictHostKeyChecking=accept-new \
    -o UserKnownHostsFile="$RUNNER_TEMP/known_hosts" -o ConnectTimeout=20 "root@$(address)" "$@"
}
fingerprint_of() { ssh-keygen -l -f "$1" | awk '{print $2, $NF}'; }

# The SSH key, replaced (Ade, 14 November: PREVIEW_SSH_KEY was exposed on 13 November). The old key connects and adds the new key's public
# half; a second connection with the new key alone must work, or the run stops with the old key still in place; then, over the new key,
# every other key goes from every authorized_keys on the server, root's and any user's, and the old key is shown refused; then Hetzner's
# ssh-key object is made again with the new public key, under the same name, for a rebuild or rescue. Fingerprints only are printed, never
# a key. Afterwards Ade puts the new key in PREVIEW_SSH_KEY, deletes PREVIEW_SSH_KEY_NEW, and runs check.
rotate_ssh_key() {
  local new="$RUNNER_TEMP/preview-ssh-new" pub old_fp new_fp files
  : "${PREVIEW_SSH_KEY_NEW:?set the secret PREVIEW_SSH_KEY_NEW in the Environment preview to the new private key}"
  install -m 600 /dev/null "$new"
  printf '%s\n' "$PREVIEW_SSH_KEY_NEW" > "$new"
  pub=$(ssh-keygen -y -P '' -f "$new" 2>/dev/null) || fail "PREVIEW_SSH_KEY_NEW doesn't parse as an unencrypted private key"
  printf '%s\n' "$pub" > "$new.pub"
  ssh-keygen -y -P '' -f "$key" > "$key.pub" 2>/dev/null || fail "PREVIEW_SSH_KEY doesn't parse as an unencrypted private key"
  old_fp=$(fingerprint_of "$key.pub"); new_fp=$(fingerprint_of "$new.pub")
  echo "Old key (PREVIEW_SSH_KEY): $old_fp"
  echo "New key (PREVIEW_SSH_KEY_NEW): $new_fp"
  [ "$old_fp" != "$new_fp" ] || fail "PREVIEW_SSH_KEY_NEW is the same key as PREVIEW_SSH_KEY"

  # Where sshd reads keys from, and the fingerprints there now.
  # shellcheck disable=SC2016 # it runs on the server, which expands it
  files='for f in /root/.ssh/authorized_keys /root/.ssh/authorized_keys2 /home/*/.ssh/authorized_keys /home/*/.ssh/authorized_keys2; do [ -f "$f" ] && echo "$f"; done
    sshd -T 2>/dev/null | sed -n "s/^authorizedkeysfile //p" | tr " " "\n" | sed "s#%u#root#g; s#%h#/root#g" | grep "^/" | while read -r f; do [ -f "$f" ] && echo "$f"; done; true'
  # shellcheck disable=SC2016 # it runs on the server, which expands it
  ssh_with "$key" 'c=$(sshd -T 2>/dev/null | sed -n "s/^authorizedkeyscommand //p"); [ -z "$c" ] || [ "$c" = none ] || echo "Note: sshd also asks an AuthorizedKeysCommand, which this run does not change."'
  echo "Keys on the server before:"
  # shellcheck disable=SC2016 # it runs on the server, which expands it
  ssh_with "$key" "for f in \$($files); do echo \"  \$f\"; ssh-keygen -l -f \"\$f\" 2>/dev/null | awk '{print \"    \" \$2, \$NF}'; done"

  # 1. The new key added with the old one, from stdin (a public key, but never on a command line either).
  # shellcheck disable=SC2016 # it runs on the server, which expands it
  ssh_with "$key" 'mkdir -p /root/.ssh && chmod 700 /root/.ssh && touch /root/.ssh/authorized_keys && chmod 600 /root/.ssh/authorized_keys
    read -r line; grep -qxF "$line" /root/.ssh/authorized_keys || printf "%s\n" "$line" >> /root/.ssh/authorized_keys' < "$new.pub"
  # 2. The new key alone, on a second connection.
  ssh_with "$new" true || fail "The new key wasn't accepted on a connection of its own. Nothing was removed: the old key still works, and Hetzner's key is unchanged."
  echo "The new key connects on its own."
  # 3. Every other key removed, over the new key: root's file holds only the new key, every other file none.
  # shellcheck disable=SC2016 # it runs on the server, which expands it
  ssh_with "$new" "read -r line; for f in \$($files); do if [ \"\$f\" = /root/.ssh/authorized_keys ]; then printf '%s\n' \"\$line\" > \"\$f\"; else : > \"\$f\"; fi; done" < "$new.pub"
  echo "Keys on the server after:"
  # shellcheck disable=SC2016 # it runs on the server, which expands it
  ssh_with "$new" "for f in \$($files); do echo \"  \$f\"; ssh-keygen -l -f \"\$f\" 2>/dev/null | awk '{print \"    \" \$2, \$NF}'; done"
  if ssh_with "$key" true 2>/dev/null; then fail "The old key is still accepted after the removal: check sshd's AuthorizedKeysFile and AuthorizedKeysCommand"; fi
  echo "The old key is refused."
  ssh_with "$new" true || fail "The new key stopped connecting after the removal"
  # 4. Hetzner's ssh-key object, made again with the new public key under the same name.
  hcloud ssh-key delete "$name" >/dev/null 2>&1 || true
  hcloud ssh-key create --name "$name" --public-key "$pub" >/dev/null
  echo "Hetzner's ssh-key $name: $(hcloud ssh-key describe "$name" -o format='{{.Fingerprint}}') (MD5, as Hetzner shows it)"
  key=$new   # the rest of this run (closing SSH) uses the new key
  summary "**The SSH key is replaced:** the server and Hetzner accept only $new_fp. Now put the new private key in PREVIEW_SSH_KEY, delete PREVIEW_SSH_KEY_NEW, and run check."
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
  local changed="Nothing was created or changed."
  if hcloud server describe "$name" >/dev/null 2>&1; then   # once provisioned: OpenBao, over SSH for this runner, closed again at the end
    ssh_key
    trap close_ssh EXIT
    open_ssh "${RUNNER_IP:?}"
    item "OpenBao" check_openbao
    item "Restarts and reboots" pending_restarts
    item "Memory" memory_use
    changed="Nothing was created. SSH was opened for this runner only, and is closed at the end with any other SSH rule (open-ssh's too)."
  fi
  alert_note
  [ "$failed" = 0 ] || fail "At least one check failed: see FAIL above. $changed"
  echo "Every check passed. $changed"
}
# Memory on the server, read only (the rebuild plan's step 0.5, Ade, 19 November 2026): the server's own figures, then the cluster's per node and pod.
# kubectl top needs k3s's bundled metrics-server; if it doesn't answer, that is said, and the item still passes, as it only reports.
memory_use() {
  # shellcheck disable=SC2016 # it runs on the server, which expands it
  on_server 'printf "\n"; free -m
    printf "== nodes\n"; k3s kubectl top nodes 2>&1 || printf "kubectl top nodes did not answer (metrics-server)\n"
    printf "== pods, by memory\n"; k3s kubectl top pods -A --sort-by=memory 2>&1 || printf "kubectl top pods did not answer (metrics-server)\n"
    printf "== requests and limits of the insys release\n"
    k3s kubectl get pods -n default -o custom-columns="POD:.metadata.name,REQ:.spec.containers[*].resources.requests.memory,LIM:.spec.containers[*].resources.limits.memory" 2>&1'
}
check_openbao() {
  local state
  wait_for_ssh
  state=$(openbao_state) || { echo "the server didn't answer: ${state:-no detail}"; return 1; }
  echo "$state"
  [[ $state == "sealed false;"* ]]
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

# Keycloak's login theme (BR-168, Ade, 22 November 2026): the console's theme insys, put on the running Preview without a Helm upgrade, the
# images, the migrations, the seed or OpenBao (which stays sealed). Only Keycloak changes: a ConfigMap of the theme, a volume and mount on its
# Deployment (so it restarts once), the realm's loginTheme, a ConfigMap noting the theme before, and a temporary user for the check. Each
# matches what the chart renders, and both are marked as the release's, so a later upgrade or reset adopts them (docs/handover-backend.md).
theme_dir=/root/insys-keycloak-theme                     # on the server: this run's scripts and the rendered ConfigMap, replaced each run
theme_files=deploy/helm/insys/files/keycloak-theme       # in the checkout, from tools/sync-keycloak-theme.sh
theme_note=insys-keycloak-theme-previous                 # on the server: the login theme before keycloak-theme set insys
theme_user=""                                            # the temporary user while it exists
theme_pending=""                                         # yes while the realm has insys and the check hasn't passed: the exit sets it back
theme_previous=""                                        # the login theme to go back to; empty is Keycloak's default
theme_version=""                                         # Keycloak's version as preflight found it, for the summary

# A script of deploy/keycloak shipped to $theme_dir, run on the server with Keycloak's admin password first on its stdin, from insys-secrets
# (it never leaves the server), then whatever this side sends: kc_input for a second line (the temporary user's password), kc_run for none.
kc_input() { # script, then its arguments
  local script=$1; shift
  on_server "{ kubectl get secret insys-secrets -o jsonpath='{.data.keycloakAdminPassword}' | base64 -d; echo; cat; } | $theme_dir/keycloak/$script $(printf '%q ' "$@")"
}
kc_run() { kc_input "$@" </dev/null; }
shown_theme() { if [ -n "$1" ]; then printf '%s' "$1"; else printf '(default)'; fi; }

# The theme in the checkout must be exactly the approved files (files/keycloak-theme/insys.sha256, Ade, 22 November 2026), before SSH opens.
theme_in_checkout() {
  [ -f "$theme_files/insys/login/theme.properties" ] && [ -s "$theme_files/insys.commit" ] \
    || fail "the checkout has no Keycloak theme: run tools/sync-keycloak-theme.sh with the console's commit, merge it, then run this on that commit"
  tools/sync-keycloak-theme.sh --check || fail "the checkout's theme isn't exactly the approved files: nothing was changed"
  echo "Preflight: the theme is in the checkout, from the console's commit $(cat "$theme_files/insys.commit"):"
  (cd "$theme_files/insys" && find . -type f | sed 's#^\./#  #' | LC_ALL=C sort)
}

# The scripts, and for keycloak-theme the theme's ConfigMap as the chart renders it (templates/keycloak-theme.yaml, with the Preview's values),
# the checksum the chart puts on Keycloak's Deployment and the approved list. Rendered here with the same helm as the server's and CI's, so it
# is the chart's own encoding; only these files go to the server, to $theme_dir, not the chart in /root/insys. The server's theme-mount.sh
# checks every shipped file's sha256 against the approved list before it applies anything.
theme_sum=""
theme_ship() { # with-theme, for keycloak-theme
  local dir="$RUNNER_TEMP/keycloak-theme"
  rm -rf "$dir" && mkdir -p "$dir/keycloak"
  cp deploy/keycloak/realm-login-theme.sh deploy/keycloak/theme-check-user.sh deploy/keycloak/theme-mount.sh "$dir/keycloak/"
  if [ "${1:-}" = with-theme ]; then
    # shellcheck disable=SC2086 # chart_set is chart()'s --set arguments, split into words as there
    render_chart() { docker run --rm -v "$PWD/deploy:/deploy:ro" -w /deploy "alpine/helm:${helm_version#v}" template insys helm/insys -f preview/values.yaml \
      $chart_set --show-only "$1"; }
    render_chart templates/keycloak-theme.yaml > "$dir/keycloak-theme.yaml" || fail "the chart didn't render the theme's ConfigMap"
    theme_sum=$(render_chart templates/keycloak.yaml | sed -n 's/^ *checksum\/keycloak-theme: \([0-9a-f]\{64\}\)$/\1/p')
    [ -n "$theme_sum" ] || fail "the chart rendered no checksum/keycloak-theme on Keycloak's Deployment"
    cp "$theme_files/insys.commit" "$theme_files/insys.sha256" "$dir/"
  fi
  tar -C "$dir" -cz . | on_server "rm -rf $theme_dir && mkdir -p $theme_dir && tar -C $theme_dir -xz"
}

# Read only: Keycloak rolled out and ready, its realm answering 200 inside the cluster, and its version 26.7.5, which the theme was made and
# checked for: the image tag on the Deployment, and the version the running server reports itself, both from its version.txt and its admin
# API. Anything else stops the run before it changes anything; any Keycloak upgrade must test the theme again first.
theme_preflight() {
  local image file api
  # shellcheck disable=SC2016 # it runs on the server, which expands it
  on_server 'kubectl rollout status deploy/insys-keycloak --timeout=120s >/dev/null && [ "$(kubectl get deploy insys-keycloak -o jsonpath="{.status.readyReplicas}")" -ge 1 ]' \
    || fail "deploy/insys-keycloak isn't rolled out and ready: nothing was changed"
  echo "Preflight: deploy/insys-keycloak is rolled out and ready."
  # shellcheck disable=SC2016 # it runs on the server, which expands it
  on_server 'pkill -f "^kubectl port-forward svc/insys-keycloak 18082:" || true
    (nohup kubectl port-forward svc/insys-keycloak 18082:8080 >/tmp/forward-theme.log 2>&1 &)
    for _ in $(seq 1 30); do code=$(curl -s -o /dev/null -w "%{http_code}" http://127.0.0.1:18082/realms/insys); [ "$code" = 200 ] && break; sleep 1; done
    pkill -f "^kubectl port-forward svc/insys-keycloak 18082:" || true
    echo "Preflight: /realms/insys inside the cluster answers HTTP $code."; [ "$code" = 200 ]' || fail "Keycloak's realm insys didn't answer 200 inside the cluster: nothing was changed"
  image=$(on_server "kubectl get deploy insys-keycloak -o jsonpath='{.spec.template.spec.containers[0].name} {.spec.template.spec.containers[0].image}'")
  file=$(on_server "kubectl exec deploy/insys-keycloak -c keycloak -- cat /opt/keycloak/version.txt" || true)
  api=$(kc_run realm-login-theme.sh insys --version || true)
  theme_version="image ${image#keycloak }; the server reports \"${file:-nothing}\" (version.txt) and ${api:-nothing} (admin API)"
  echo "Preflight: Keycloak's version: $theme_version."
  [[ $image == "keycloak quay.io/keycloak/keycloak:26.7.5" ]] && [ "$file" = "Keycloak - Version 26.7.5" ] && [ "$api" = 26.7.5 ] \
    || fail "Keycloak isn't 26.7.5, the version the theme was made and checked for ($theme_version): nothing was changed. Test the theme on the new version first."
}

# The login theme now, noted on the server before it changes, so a revert later knows it. A rerun, with insys already set, keeps the first note.
theme_record() {
  local current note
  current=$(kc_run realm-login-theme.sh insys --show) || fail "couldn't read the realm's login theme: nothing was changed"
  [[ $current =~ ^[A-Za-z0-9._-]+$|^\(default\)$ ]] || fail "the realm's login theme reads \"$current\""
  [ "$current" = "(default)" ] && current=""
  note=$(on_server "kubectl get configmap $theme_note -o json 2>/dev/null" || true)
  if [ "$current" != insys ]; then
    on_server "kubectl create configmap $theme_note --from-literal=loginTheme=$current --from-literal=recordedAt=$(date -u +%FT%TZ) \
      --from-literal=run=${GITHUB_RUN_ID:-local} --dry-run=client -o yaml | kubectl apply -f - >/dev/null"
    theme_previous=$current
    echo "Previous login theme: $(shown_theme "$current"), recorded on the server in ConfigMap $theme_note."
  elif [ -n "$note" ]; then
    theme_previous=$(jq -r '.data.loginTheme // ""' <<<"$note")
    echo "The login theme is already insys; the one before, recorded by run $(jq -r '.data.run // "?"' <<<"$note") at $(jq -r '.data.recordedAt // "?"' <<<"$note"): $(shown_theme "$theme_previous")."
  else
    theme_previous=""
    echo "::warning::The login theme is already insys and nothing recorded the one before: a revert would set Keycloak's default."
  fi
}
theme_recorded() { # the recorded previous theme, or empty for none (Keycloak's default)
  local note
  note=$(on_server "kubectl get configmap $theme_note -o json 2>/dev/null" || true)
  if [ -n "$note" ]; then jq -r '.data.loginTheme // ""' <<<"$note"; fi
}

theme_set() { # the theme; empty for Keycloak's default
  kc_run realm-login-theme.sh insys "${1:---default}"
}

# Keycloak restarted, and waited for: production start caches themes, so it must start after the theme is mounted and before the login theme
# is set; and again after any revert (Ade, 22 November 2026), so no page comes from the cache of the theme just left.
theme_restart() { # why
  echo "Restarting Keycloak: $1."
  on_server "kubectl rollout restart deploy/insys-keycloak >/dev/null && kubectl rollout status deploy/insys-keycloak --timeout=900s >/dev/null"
}
# Back to the recorded theme, then a restart: on a failed check, and by keycloak-theme-revert.
theme_back() {
  theme_set "$theme_previous" && theme_restart "after setting the login theme back to $(shown_theme "$theme_previous")" && theme_pending=""
}

theme_cleanup() { # the temporary user: its one-time code credential, then the user, whatever happened
  [ -n "$theme_user" ] || return 0
  if kc_run theme-check-user.sh insys delete "$theme_user"; then
    theme_user=""
  else
    echo "::warning::The temporary user $theme_user couldn't be removed; the next keycloak-theme run removes it first, or remove it in Keycloak's admin console."
  fi
}

# The check (Ade's step 3): sign-in, two-factor setup and a one-time code, in a browser on this runner through Keycloak's public address, as
# a dedicated temporary Keycloak-only user, never an account the team uses (the sample accounts' passwords aren't on the server anyway). Its
# password is random, masked, and only on stdin; the browser reads the two-factor secret from the page, masks it and each code, and never
# prints them. Pass or fail per step, then per check: sign-in, two-factor setup, one-time code.
# PREVIEW_THEME_CHECK_URL points the browser at another Keycloak, for a rehearsal away from the Preview; the workflow never sets it.
theme_check() { # the login theme the pages must be served with
  local expect=$1 password status=0 tls=() issuer
  password=$(openssl rand -hex 24)
  mask "$password"
  : > "$RUNNER_TEMP/theme-check.txt"
  issuer=$(echo | openssl s_client -connect "$(address):443" -servername "id.$PREVIEW_DOMAIN" 2>/dev/null | openssl x509 -noout -issuer 2>/dev/null || true)
  if ! issued_by "$issuer" production; then
    tls=(--ignore-https-errors)
    echo "id.$PREVIEW_DOMAIN has no Let's Encrypt production certificate ($issuer): the browser accepts it for this check."
  fi
  theme_user="theme-check-${GITHUB_RUN_ID:-local}"
  printf '%s\n' "$password" | kc_input theme-check-user.sh insys create "$theme_user" || { echo "FAIL  the temporary user wasn't made"; theme_cleanup; return 1; }
  printf '%s\n' "$password" | node deploy/keycloak/theme-check.cjs --url "${PREVIEW_THEME_CHECK_URL:-https://id.$PREVIEW_DOMAIN}" --realm insys --username "$theme_user" \
    --theme "$expect" "${tls[@]}" | tee "$RUNNER_TEMP/theme-check.txt" || status=1
  theme_cleanup
  return "$status"
}
theme_check_summary() { # the three checks' results, then every step
  local results
  results=$(sed -n 's/^RESULT  \(.*\)$/- \1/p' "$RUNNER_TEMP/theme-check.txt" 2>/dev/null)
  summary "Keycloak's version: $theme_version."
  summary "${results:-- sign-in, two-factor setup, one-time code: not reached (FAIL)}"
  summary "$(grep -E '^(PASS|FAIL)  ' "$RUNNER_TEMP/theme-check.txt" 2>/dev/null | sed 's/^/    /' || echo "    no steps ran")"
}

# On any exit: the temporary user goes, and if insys is set but its check hasn't passed, the previous theme comes back with a restart; then
# SSH closes.
theme_exit() {
  theme_cleanup || true
  if [ "$theme_pending" = yes ]; then
    echo "Setting the login theme back to $(shown_theme "$theme_previous"), as the check didn't pass."
    theme_back || echo "::error::The login theme couldn't be set back to $(shown_theme "$theme_previous") with a restart: run keycloak-theme-revert now."
  fi
  close_ssh
}

keycloak_theme() {
  local at mounted
  theme_ship with-theme
  theme_preflight
  theme_record
  echo "Mounting the theme from the console's commit $(cat "$theme_files/insys.commit")."
  mounted=$(on_server "$theme_dir/keycloak/theme-mount.sh insys $theme_dir/keycloak-theme.yaml $theme_sum $theme_dir/insys.sha256") \
    || { printf '%s\n' "$mounted"; fail "the theme wasn't mounted; the login theme is unchanged"; }
  printf '%s\n' "$mounted"
  # The mount restarted Keycloak when it changed the Deployment; when it was already in place, Keycloak restarts here, so it surely starts
  # with the mounted files before the login theme names them.
  case "$mounted" in *"unchanged, no restart"*) theme_restart "so it starts with the mounted theme, before the login theme is set" ;; esac
  theme_pending=yes
  theme_set insys
  at=$(date -u '+%F %T UTC')
  if theme_check insys; then
    theme_pending=""
    summary "**Keycloak's login theme is insys**, set at $at, from the console's commit \`$(cat "$theme_files/insys.commit")\`. The check passed, as a temporary user (its one-time code and the user removed). Before it: $(shown_theme "$theme_previous") (recorded in ConfigMap $theme_note)."
    theme_check_summary
  else
    echo "The check failed: setting the login theme back to $(shown_theme "$theme_previous") and restarting Keycloak now."
    theme_back || true
    summary "**The login theme check failed**, so the login theme is back to $(shown_theme "$theme_previous") and Keycloak restarted (insys was set at $at, from the console's commit \`$(cat "$theme_files/insys.commit")\`). The theme stays mounted, harmless without the login theme."
    theme_check_summary
    fail "the login theme check failed (see FAIL above); the login theme is back to $(shown_theme "$theme_previous")"
  fi
}

keycloak_theme_revert() {
  local at
  theme_ship
  theme_preflight
  theme_previous=$(theme_recorded)
  theme_set "$theme_previous"
  at=$(date -u '+%F %T UTC')
  theme_restart "after setting the login theme back to $(shown_theme "$theme_previous")"
  if theme_check "${theme_previous:-keycloak.v2}"; then
    summary "**Keycloak's login theme is back to $(shown_theme "$theme_previous")**, set at $at, and Keycloak restarted. The check passed, as a temporary user (its one-time code and the user removed)."
    theme_check_summary
  else
    summary "**Keycloak's login theme is $(shown_theme "$theme_previous")**, set at $at, and Keycloak restarted, but **its check failed**."
    theme_check_summary
    fail "the check on the login theme $(shown_theme "$theme_previous") failed (see FAIL above)"
  fi
}

if [ "$action" = check ]; then
  check
  exit 0
fi

trap close_ssh EXIT
case "$action" in
  provision)
    ssh_key; infrastructure; open_ssh "${RUNNER_IP:?}"; wait_for_ssh; os_policy; cluster; images; server_secrets; pull_secret; chart; openbao; encrypt; seed; encrypt; check_tls
    ;;
  upgrade)
    ssh_key; open_ssh "${RUNNER_IP:?}"; wait_for_ssh; os_policy; cluster; images; pull_secret; chart; upgrade_check; check_tls
    ;;
  upgrade-check)    # read only: the upgrade check against the version serving now, nothing deployed
    ssh_key; open_ssh "${RUNNER_IP:?}"; wait_for_ssh; upgrade_check
    ;;
  read-authority)   # read only: OFTZA's STATIC_DATA_T1 authority and the demo users' roles, in a read-only database session
    ssh_key; open_ssh "${RUNNER_IP:?}"; wait_for_ssh; read_authority
    ;;
  open-ssh)
    trap - EXIT
    open_ssh "${2:?the address to allow}"
    echo "SSH is open from $2 until the next run closes it."
    ;;
  reset)
    ssh_key; open_ssh "${RUNNER_IP:?}"; wait_for_ssh; os_policy
    on_server "KUBECONFIG=/etc/rancher/k3s/k3s.yaml helm uninstall insys --wait || true; kubectl delete pvc --all --wait=true; kubectl delete secret insys-secrets --ignore-not-found"
    cluster; images; server_secrets; pull_secret; chart; openbao; encrypt; seed; encrypt; check_tls
    ;;
  diagnose)
    ssh_key; open_ssh "${RUNNER_IP:?}"; wait_for_ssh; diagnose
    ;;
  rotate-ssh-key)
    ssh_key; open_ssh "${RUNNER_IP:?}"; wait_for_ssh; rotate_ssh_key
    ;;
  os-update)
    ssh_key; open_ssh "${RUNNER_IP:?}"; wait_for_ssh; os_policy; os_update
    ;;
  reboot)
    ssh_key; open_ssh "${RUNNER_IP:?}"; wait_for_ssh; os_policy; reboot_server
    ;;
  backup)
    ssh_key; open_ssh "${RUNNER_IP:?}"; wait_for_ssh; backup_now
    ;;
  restore)
    [ -n "${2:-}" ] || fail "set ssh_from to your own address: you unseal OpenBao over SSH after the restore"
    ssh_key; open_ssh "${RUNNER_IP:?}"; wait_for_ssh; restore
    close_ssh; open_ssh "$2"   # the runner's rule goes; only yours stays, for the unseal
    trap - EXIT                # the next run closes it
    echo "OpenBao is sealed, as after any restore. SSH is open from $2: unseal it as the handover's Unsealing OpenBao says, with the key of the OpenBao"
    echo "this backup came from: $(openbao_file "$(on_server "cat $openbao_run_note 2>/dev/null" || true)"), if the backup is from that day or later."
    echo "Then run backup, to check the counts and that a stored data key decrypts."
    ;;
  rotate-passwords)
    ssh_key; open_ssh "${RUNNER_IP:?}"; wait_for_ssh; seed; encrypt
    ;;
  keycloak-theme)   # Keycloak only: no images, chart, migrations, seed, OpenBao or encrypt
    theme_in_checkout
    ssh_key; open_ssh "${RUNNER_IP:?}"; wait_for_ssh
    trap theme_exit EXIT
    keycloak_theme
    ;;
  keycloak-theme-revert)
    ssh_key; open_ssh "${RUNNER_IP:?}"; wait_for_ssh
    trap theme_exit EXIT
    keycloak_theme_revert
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
