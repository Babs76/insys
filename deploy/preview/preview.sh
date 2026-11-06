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
  tar -C deploy -cz helm/insys preview/values.yaml preview/backup.yaml openbao/openbao.sh keycloak/realm-name.sh keycloak/user-admin-secret.sh keycloak/realm-smtp.sh | on_server "rm -rf /root/insys && mkdir -p /root/insys && tar -C /root/insys -xz"
  on_server "cd /root/insys && KUBECONFIG=/etc/rancher/k3s/k3s.yaml helm upgrade --install insys helm/insys -f preview/values.yaml \
    --set ingress.host=$PREVIEW_DOMAIN,ingress.apiPath=/api/,console.environmentName=preview,keycloak.ingressHost=id.$PREVIEW_DOMAIN,keycloak.hostname=https://id.$PREVIEW_DOMAIN \
    --set authentication.authority=http://insys-keycloak:8080/realms/insys,authentication.requireHttpsMetadata=false \
    --set authentication.issuer=https://id.$PREVIEW_DOMAIN/realms/insys,console.issuer=https://id.$PREVIEW_DOMAIN/realms/insys"
  on_server "kubectl wait --for=condition=complete job -l app.kubernetes.io/name=migrate --timeout=900s && kubectl rollout status statefulset/insys-openbao --timeout=600s"
  on_server "kubectl apply -f /root/insys/preview/backup.yaml >/dev/null"
  realm_name
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
  forwards
  ssh -i "$key" -o UserKnownHostsFile="$RUNNER_TEMP/known_hosts" -fN -L 18080:127.0.0.1:18080 -L 18081:127.0.0.1:18081 "root@$(address)"
  for url in http://127.0.0.1:18080/health/ready http://127.0.0.1:18081/realms/insys; do   # the runner's end of the tunnel too
    for _ in $(seq 1 30); do code=$(curl -s -o /dev/null -w '%{http_code}' "$url"); [ "$code" = 200 ] && continue 2; sleep 1; done
    fail "through the runner's tunnel, $url answered HTTP $code, not 200"
  done
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
  echo "OpenBao (sealed; pod restarts):"
  on_server "/root/insys/openbao/openbao.sh sealed insys; kubectl get pod insys-openbao-0 -o jsonpath='{.status.containerStatuses[0].restartCount}{\"\\n\"}'" || true
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

# OpenBao unsealed, and a data key the database holds unwrapped by Transit with the API's own token: so what was encrypted before reads
# after. The key's plaintext is never printed; its ciphertext is useless without OpenBao.
kms_check() {
  local sealed wrapped
  sealed=$(on_server "/root/insys/openbao/openbao.sh sealed insys")
  [ "$sealed" = false ] || fail "OpenBao is sealed: unseal it (the handover's Unsealing OpenBao), then run this again"
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
backup_now() {
  local day
  day=$(date -u +%F)
  # shellcheck disable=SC2016 # it runs on the server, which expands it
  on_server 'job=insys-backup-$(date -u +%H%M%S); kubectl create job "$job" --from=cronjob/insys-backup >/dev/null && kubectl wait --for=condition=complete "job/$job" --timeout=900s >/dev/null' \
    || fail "the backup job didn't complete"
  echo "Backup of $day, on the server in /var/backups/insys:"
  on_server "ls -l /var/backups/insys | awk '/$day/ {print \"  \" \$NF \", \" \$5 \" bytes\"}'"
  for db in insys keycloak; do
    echo "  $day-$db.dump lists $(on_server "kubectl exec -i insys-postgres-0 -- pg_restore --list < /var/backups/insys/$day-$db.dump | grep -vc '^;'") items"
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
  backup)
    ssh_key; open_ssh "${RUNNER_IP:?}"; wait_for_ssh; backup_now
    ;;
  restore)
    [ -n "${2:-}" ] || fail "set ssh_from to your own address: you unseal OpenBao over SSH after the restore"
    ssh_key; open_ssh "${RUNNER_IP:?}"; wait_for_ssh; restore
    close_ssh; open_ssh "$2"   # the runner's rule goes; only yours stays, for the unseal
    trap - EXIT                # the next run closes it
    echo "OpenBao is sealed, as after any restore. SSH is open from $2: unseal it as the handover's Unsealing OpenBao says, with the key from the file"
    echo "of the OpenBao this backup came from (run 41835262697's), then run backup to check the counts and that a stored data key decrypts."
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
