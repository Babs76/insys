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
# ADR-027's proof of cloud neutrality: on a throwaway cluster, with the images loaded as insys-api:smoke and insys-migrator:smoke,
# deploys the chart with every dependency in the cluster and proves through the deployed API that migrations ran and the API uses non-owner
# roles, sign-in goes through Keycloak, a protected field is written and read back through OpenBao, and a document is stored in and read
# from the object store, and that OpenBao and Keycloak keep their keys and accounts across a restart (register row 1), OpenBao unsealed with
# throwaway keys, and that the console serves each app with its config, deep links and headers. It says nothing about a particular provider's
# services, load or failover.
set -euo pipefail
here="$(cd "$(dirname "$0")" && pwd)"
tenant=0199f000-0000-7000-8000-00000000abcd
api=http://insys-api:8080/api/v1
kc=http://insys-keycloak:8080
fail() { echo "SMOKE FAILED: $*" >&2; exit 1; }
diagnose() {
  kubectl get pods -o wide || true
  kubectl describe pod -l app.kubernetes.io/name=api | tail -25 || true
  kubectl logs deploy/insys-api --tail=60 || true
  kubectl logs job/insys-migrate-1 --tail=30 || true
  kubectl logs deploy/insys-clamav --tail=30 || true
  kubectl describe pod -l app.kubernetes.io/name=console | sed -n '/^Events:/,$p' | tail -15 || true
}
trap 'diagnose' ERR

# The Secret made outside the chart, as the Preview's server makes it (existingSecret): a template reading a secret from values instead would
# get nothing, as SeaweedFS's S3 keys did (rotate-passwords run 42080719155). Throwaway values for a throwaway cluster, not secrets.
kubectl create secret generic insys-secrets \
  --from-literal=migrationConnection='Host=insys-postgres;Database=insys;Username=insys_owner;Password=smoke-owner' \
  --from-literal=connectionTemplate='Host=insys-postgres;Database=insys;Username={role};Password=smoke-role' \
  --from-literal=rolePassword=smoke-role --from-literal=hashKey=c21va2Utc21va2Utc21va2Utc21va2Utc21va2Utc21vaw== \
  --from-literal=kmsToken=smoke-kms-token --from-literal=objectStoreAccessKey=smoke-access --from-literal=objectStoreSecretKey=smoke-secret-key \
  --from-literal=postgresPassword=smoke-owner --from-literal=keycloakAdminPassword=smoke-admin > /dev/null
helm install insys "$here/../helm/insys" -f "$here/values.yaml"
kubectl wait --for=condition=complete job/insys-migrate-1 --timeout=600s
kubectl rollout status statefulset/insys-openbao --timeout=300s

echo "== OpenBao initialised, unsealed and configured, its keys kept only in this shell"
init=$("$here/../openbao/openbao.sh" init insys)
unseal_key=$(jq -r '.keys_base64[0]' <<<"$init")
printf '%s' "$unseal_key" | "$here/../openbao/openbao.sh" unseal insys
kms_token=$(jq -r .root_token <<<"$init" | "$here/../openbao/openbao.sh" configure insys transit insys)
[ "$("$here/../openbao/openbao.sh" sealed insys)" = false ] || fail "OpenBao is still sealed"
jq -nc --arg t "$kms_token" '{stringData: {kmsToken: $t}}' | kubectl patch secret insys-secrets --type merge --patch-file /dev/stdin > /dev/null
kubectl rollout restart deploy/insys-api
for d in insys-keycloak insys-api; do kubectl rollout status "deploy/$d" --timeout=600s; done
kubectl rollout status statefulset/insys-seaweedfs --timeout=300s

# It outlives the seed (SEED_TIMEOUT): after the whole seed's two hours a one-hour pod had exited, and the sign-in check after it couldn't exec (run 44699166104).
kubectl run smoke --image=alpine:3.20 --restart=Never --command -- sleep $(( ${SEED_TIMEOUT:-1500} + 3600 ))
kubectl wait --for=condition=ready pod/smoke --timeout=120s
kubectl exec smoke -- apk add -q curl jq
in_cluster() { kubectl exec smoke -- sh -c "$1"; }
sql() { kubectl exec -i insys-postgres-0 -- psql -U insys_owner -d insys -v ON_ERROR_STOP=1 "$@"; }
query() { sql -Atc "$1"; }

echo "== Migrations ran, and the API connects on non-owner roles"
[ "$(query "SELECT count(*) FROM pg_tables WHERE schemaname = 'party'")" -gt 0 ] || fail "no party tables"
[ "$(query "SELECT count(*) FROM pg_stat_activity WHERE datname = 'insys' AND usename LIKE 'insys\_%' AND usename <> 'insys_owner'")" -gt 0 ] || fail "the API has no module role connection"
[ "$(query "SELECT count(*) FROM pg_stat_activity a JOIN pg_roles r ON r.rolname = a.usename WHERE a.datname = 'insys' AND a.usename <> 'insys_owner' AND a.usename <> 'postgres' AND (r.rolsuper OR r.rolbypassrls)")" = 0 ] || fail "a superuser or BYPASSRLS role is in use (DBA-002)"
[ "$(query "SELECT count(*) FROM pg_tables WHERE tableowner LIKE 'insys\_%' AND tableowner <> 'insys_owner'")" = 0 ] || fail "a module role owns tables"

echo "== Sign-in through Keycloak"
# The sign-in page carries the realm's name in its title: from the values (keycloak.realmDisplayName) on import, then through the admin API on the
# existing realm (deploy/keycloak/realm-name.sh, as the Preview does), twice, the second changing nothing. The page is held whole, not piped into
# grep -q: under pipefail, grep stopping at the match fails the pipe.
login="$kc/realms/insys/protocol/openid-connect/auth?client_id=insys-web&response_type=code&scope=openid&redirect_uri=http%3A%2F%2Flocalhost%3A5173%2F&code_challenge_method=S256&code_challenge=E9Melhoa2OwvFrEMTJguCHaoeK1t8URWbuGJSstw-cM"
sign_in_title() {
  local page title
  page=$(in_cluster "curl -s -w '\nHTTP %{http_code}' '$login'")
  title=$(grep -m1 -i '<title' <<<"$page" || true)
  case "$title" in *"$1"*) ;; *) fail "the sign-in page doesn't show \"$1\": $(tail -n1 <<<"$page"), title: ${title:-none}" ;; esac
}
sign_in_title "INSYS smoke test (sample data only)"
renamed="INSYS smoke test, renamed (sample data only)"
[ "$(printf '%s' smoke-admin | "$here/../keycloak/realm-name.sh" insys insys "$renamed")" = "The realm insys is now named \"$renamed\"." ] || fail "the realm wasn't renamed"
[ "$(printf '%s' smoke-admin | "$here/../keycloak/realm-name.sh" insys insys "$renamed")" = "The realm insys is already named \"$renamed\"." ] || fail "naming the realm again changed it"
sign_in_title "$renamed"
admin=$(in_cluster "curl -sf -d grant_type=password -d client_id=admin-cli -d username=admin -d password=smoke-admin $kc/realms/master/protocol/openid-connect/token | jq -r .access_token")
auth="Authorization: Bearer $admin"
subject=$(in_cluster "curl -si -H '$auth' -H 'Content-Type: application/json' $kc/admin/realms/insys/users -d '{\"username\":\"smoke\",\"enabled\":true,\"email\":\"smoke@example.test\",\"emailVerified\":true,\"firstName\":\"Smoke\",\"lastName\":\"Test\",\"attributes\":{\"tenant_id\":[\"$tenant\"]},\"credentials\":[{\"type\":\"password\",\"value\":\"smoke-pass\",\"temporary\":false}]}' | tr -d '\r' | sed -n 's#^[Ll]ocation: .*/##p'")
[ -n "$subject" ] || fail "Keycloak didn't create the user"
# The realm allows the browser flow only. A smoke test has no browser, so this throwaway cluster's client also takes a password.
client=$(in_cluster "curl -sf -H '$auth' '$kc/admin/realms/insys/clients?clientId=insys-web' | jq -r '.[0].id'")
in_cluster "curl -sf -H '$auth' $kc/admin/realms/insys/clients/$client | jq '.directAccessGrantsEnabled = true' | curl -sf -X PUT -H '$auth' -H 'Content-Type: application/json' -d @- $kc/admin/realms/insys/clients/$client"
sql -v tenant="$tenant" -v subject="$subject" < "$here/seed.sql" > /dev/null
token() { in_cluster "curl -sf -d grant_type=password -d client_id=insys-web -d username=smoke -d password=smoke-pass -d 'scope=openid insys-api' $kc/realms/insys/protocol/openid-connect/token | jq -r .access_token"; }
user=$(token)
[ -n "$user" ] && [ "$user" != null ] || fail "no token from Keycloak"
call() { # method path body [extra header]; prints the body, fails on anything but 2xx
  in_cluster "curl -sS --fail-with-body -X $1 -H 'Authorization: Bearer $user' -H 'Content-Type: application/json' -H 'Idempotency-Key: smoke-$RANDOM$RANDOM' ${4:+-H '$4'} ${3:+-d '$3'} $api$2"
}
[ "$(in_cluster "curl -s -o /dev/null -w '%{http_code}' $api/party/parties")" = 401 ] || fail "the API answers without a token"

echo "== The API makes a user's Keycloak account (Identity:Keycloak, inside the cluster)"
# The client's secret, generated by Keycloak on import, copied into the Secret as the Preview does; the API restarts to read it.
printf '%s' smoke-admin | "$here/../keycloak/user-admin-secret.sh" insys insys
user=$(token)
provisioned=$(call POST /identity/users '{"username":"provisioned","displayName":"Provisioned by the API","idpCode":"KEYCLOAK","mfaEnrolled":false,"email":"provisioned@example.test"}' | jq -r .id)
[ -n "$provisioned" ] && [ "$provisioned" != null ] || fail "the API didn't make the user"
admin=$(in_cluster "curl -sf -d grant_type=password -d client_id=admin-cli -d username=admin -d password=smoke-admin $kc/realms/master/protocol/openid-connect/token | jq -r .access_token")
for _ in $(seq 1 30); do
  found=$(in_cluster "curl -sf -H 'Authorization: Bearer $admin' '$kc/admin/realms/insys/users?username=smoke.provisioned&exact=true' | jq length")
  [ "$found" = 1 ] && break
  sleep 2
done
[ "$found" = 1 ] || { query "SELECT consumer, status, prerequisite_code FROM integration.parked_delivery"; fail "Keycloak has no account for the user the API made"; }
[ "$(query "SELECT count(*) FROM integration.parked_delivery WHERE prerequisite_code = 'KEYCLOAK_UNAVAILABLE'")" = 0 ] || fail "a delivery is parked for Keycloak"

echo "== A protected field through OpenBao"
party=$(call POST /party/parties '{"individual":{"firstNames":"Smoke","surname":"Test","birthDate":"1980-01-01"},"confirmNew":true,"reason":"Smoke test"}' | jq -r .id)
etag=$(in_cluster "curl -si -H 'Authorization: Bearer $user' $api/party/parties/$party | tr -d '\r' | sed -n 's/^[Ee][Tt]ag: //p'")
call POST "/party/parties/$party/identifiers" '{"docType":"NATIONAL_ID","country":"ZA","number":"8001015009087"}' "If-Match: $etag" > /dev/null
[ "$(query "SELECT count(*) FROM integration.field_data_key WHERE master_key_ref = 'openbao:transit:insys'")" -gt 0 ] || fail "no data key is protected by OpenBao"
# Nothing else holds the number or its data key once the API restarts: reading it needs Transit to decrypt the data key.
query "UPDATE party.party_identifier SET number_last4 = NULL WHERE party_id = '$party'" > /dev/null
kubectl rollout restart deploy/insys-api
kubectl rollout status deploy/insys-api --timeout=300s
user=$(token)
call GET "/party/parties/$party" | jq -r '.identifiers[0].maskedNumber' | grep -q 9087 || fail "the number didn't decrypt to its last four"
[ "$(query "SELECT count(*) FROM party.party_identifier WHERE party_id = '$party' AND number_enc LIKE '%8001015009087%'")" = 0 ] || fail "the number is stored in clear"

echo "== OpenBao and Keycloak keep their keys and accounts across a restart"
kubectl delete pod insys-openbao-0 --wait=true > /dev/null
kubectl wait --for=condition=ready pod/insys-openbao-0 --timeout=300s
[ "$("$here/../openbao/openbao.sh" sealed insys)" = true ] || fail "OpenBao came back unsealed: something holds its key"
printf '%s' "$unseal_key" | "$here/../openbao/openbao.sh" unseal insys
kubectl rollout restart deploy/insys-keycloak
kubectl rollout status deploy/insys-keycloak --timeout=600s
user=$(token)
[ -n "$user" ] && [ "$user" != null ] || fail "the account didn't survive Keycloak's restart"
query "UPDATE party.party_identifier SET number_last4 = NULL WHERE party_id = '$party'" > /dev/null
kubectl rollout restart deploy/insys-api
kubectl rollout status deploy/insys-api --timeout=300s
call GET "/party/parties/$party" | jq -r '.identifiers[0].maskedNumber' | grep -q 9087 || fail "after OpenBao's restart, the number didn't decrypt"

echo "== The console: each app with its config, deep links to the apps, the headers"
kubectl rollout status deploy/insys-console --timeout=300s
console=http://insys-console:8080
[ "$(in_cluster "curl -sf $console/config.json | jq -r .keycloak.clientId")" = insys-web ] || fail "the back office has no config.json"
[ "$(in_cluster "curl -sf $console/broker/config.json | jq -r .keycloak.clientId")" = broker-portal ] || fail "the broker portal has no config.json"
in_cluster "curl -sf $console/health/schemes" | grep -qi '<html' || fail "a deep link under /health/ didn't reach the app"
in_cluster "curl -sf $console/member/policies" | grep -qi '<html' || fail "a portal's deep link didn't reach its app"
in_cluster "curl -sI $console/" | tr -d '\r' | grep -i '^content-security-policy:' | grep -q "connect-src 'self' https://insys.smoke.test https://id.smoke.test" || fail "the CSP doesn't name the API's and Keycloak's origins"
[ "$(in_cluster "curl -s -o /dev/null -w '%{http_code}' $console/assets/none.js")" = 404 ] || fail "a missing asset isn't a 404"

echo "== A document in the object store"
content=$(printf 'smoke document' | base64)
document=$(call POST /documents/documents "{\"categoryCode\":\"KYC\",\"businessRefType\":\"PARTY\",\"businessRefId\":\"$party\",\"mimeType\":\"text/plain\",\"contentBase64\":\"$content\",\"fileName\":\"smoke.txt\"}" | jq -r .id)
# AIA-010: an upload is held until ClamAV clears it; the scan job runs every 30 seconds. The EICAR test file is held for good.
status() { in_cluster "curl -s -o /dev/null -w '%{http_code}' -H 'Authorization: Bearer $user' $api$1"; }
kubectl rollout status deploy/insys-clamav --timeout=900s
eicar=$(printf '%s' 'X5O!P%@AP[4\PZX54(P^)7CC)7}$EICAR-STANDARD-ANTIVIRUS-TEST-FILE!$H+H*' | base64 -w0)
infected=$(call POST /documents/documents "{\"categoryCode\":\"KYC\",\"businessRefType\":\"PARTY\",\"businessRefId\":\"$party\",\"mimeType\":\"text/plain\",\"contentBase64\":\"$eicar\",\"fileName\":\"eicar.txt\"}" | jq -r .id)
for _ in $(seq 1 30); do
  [ "$(call GET "/documents/documents/$document" | jq -r .scanStatus)" = CLEAN ] && [ "$(call GET "/documents/documents/$infected" | jq -r .scanStatus)" != PENDING ] && break
  sleep 10
done
[ "$(call GET "/documents/documents/$document" | jq -r .scanStatus)" = CLEAN ] || fail "ClamAV didn't clear the upload"
[ "$(call GET "/documents/documents/$document/content?purpose=SMOKE")" = "smoke document" ] || fail "the document didn't read back"
[ "$(call GET "/documents/documents/$infected" | jq -r .scanStatus)" = INFECTED ] || fail "ClamAV didn't flag the EICAR test file"
[ "$(status "/documents/documents/$infected/content?purpose=SMOKE")" = 409 ] || fail "the infected upload was released"
kubectl exec insys-seaweedfs-0 -- sh -c "wget -qO- --header 'Accept: application/json' http://\$(hostname -i):8888/buckets/insys-documents/" | jq -e '.Entries | length > 0' > /dev/null || fail "the bucket holds nothing"

echo "== The OFTZA seed through Keycloak, as the Preview runs it (batch 6, Ade 19 and 20 November 2026)"
# The release is upgraded to the Preview's settings (values-seed.yaml: the Preview environment, declared non-production, the operator tenant
# and its two administrators bootstrapped), and the seed runs with --keycloak-url as preview.sh's seed() does: the realm's insys-seed client
# enabled for the run only, every persona signing in through Keycloak. The workflow builds the seed (SEED_DLL); it runs inside the cluster, in a
# pod of the API's own image (which holds the .NET runtime). The whole seed is too slow for this job, so the smoke test runs
# SEED_ONLY=administrators (Ade, 20 November 2026, option (a)): the tenant and its two first administrators, each signing in through Keycloak
# and reading /identity/me. The whole seed runs in its own manual workflow (full-seed.yml, option (c)), with SEED_ONLY empty and a long
# SEED_TIMEOUT. The seed prints each stage's elapsed time, and why a step waited its whole settle, on stderr; its stage lines go to the summary.
seed_dll=${SEED_DLL:?the workflow builds tools/Insys.Seed and gives its dll}
seed_only=${SEED_ONLY-administrators}
seed_timeout=${SEED_TIMEOUT:-1500}
helm upgrade insys "$here/../helm/insys" -f "$here/values.yaml" -f "$here/values-seed.yaml"
kubectl wait --for=condition=complete job/insys-migrate-2 --timeout=600s
kubectl rollout status deploy/insys-api --timeout=600s
pending_events() { # each module outbox's unpublished events by type and attempts: event types and counts only
  for s in $(query "SELECT schemaname FROM pg_tables WHERE tablename = 'outbox_event' ORDER BY 1"); do
    query "SELECT '$s', event_type, publish_attempts, count(*) FROM $s.outbox_event WHERE published_at IS NULL GROUP BY 2, 3" || true
  done
}
progress=$(mktemp)
stages_summary() { # the seed's own stage lines only (stage names, seconds, 429 counts): no request or response content
  [ -z "${GITHUB_STEP_SUMMARY:-}" ] || { printf '\nThe seed'"'"'s stages:\n```\n'; grep -E '^seed: ' "$progress" | cut -c1-200 || true; printf '```\n'; } >> "$GITHUB_STEP_SUMMARY"
}
kubectl run seed --image=insys-api:smoke --image-pull-policy=Never --restart=Never --command -- sleep $(( seed_timeout + 600 ))
kubectl wait --for=condition=ready pod/seed --timeout=120s
kubectl cp "$(dirname "$seed_dll")" seed:/tmp/seed > /dev/null
mode=${seed_only:+--only $seed_only}
started=$(date +%s)
timeout "$seed_timeout" kubectl exec seed -- env INSYS_SEED_KEYCLOAK_ADMIN_PASSWORD=smoke-admin dotnet /tmp/seed/Insys.Seed.dll --api http://insys-api:8080 \
  --keycloak-url http://insys-keycloak:8080 --keycloak-admin admin --operator-tenant 0199f000-0000-7000-8000-000000000001 \
  --platform-1 insys-admin --platform-2 insys-admin-2 $mode 2>"$progress" \
  || { code=$?; cat "$progress" >&2; stages_summary; kubectl logs deploy/insys-api --tail=80 || true; pending_events; fail "the seed failed against the deployed API (exit $code; 124 is the ${seed_timeout}s limit)"; }
seconds=$(( $(date +%s) - started ))
cat "$progress" >&2
echo "The seed ran in ${seconds}s (${mode:-the whole seed}, every call through Keycloak)."
[ -z "${GITHUB_STEP_SUMMARY:-}" ] || echo "Smoke: the OFTZA seed (${mode:-the whole seed}) ran against the deployed API through Keycloak in ${seconds}s." >> "$GITHUB_STEP_SUMMARY"
stages_summary
kubectl delete pod seed --wait=false > /dev/null

# The seed disabled its client again.
admin=$(in_cluster "curl -sf -d grant_type=password -d client_id=admin-cli -d username=admin -d password=smoke-admin $kc/realms/master/protocol/openid-connect/token | jq -r .access_token")
[ "$(in_cluster "curl -sf -H 'Authorization: Bearer $admin' '$kc/admin/realms/insys/clients?clientId=insys-seed' | jq -r '.[0].enabled'")" = false ] \
  || fail "the seed left its Keycloak client insys-seed enabled"

# One seeded user signs in through the realm (insys-web, which this cluster lets take a password) with a throwaway password set here, and reads
# as itself: with --only administrators, the first administrator (oftza.oftza-admin-1); after the whole seed, the auditor persona
# (oftza.oftza-auditor). Identity made both Keycloak accounts.
signer=oftza.oftza-auditor
[ "$seed_only" != administrators ] || signer=oftza.oftza-admin-1
persona_id=$(in_cluster "curl -sf -H 'Authorization: Bearer $admin' '$kc/admin/realms/insys/users?username=$signer&exact=true' | jq -r '.[0].id'")
[ -n "$persona_id" ] && [ "$persona_id" != null ] || fail "Keycloak has no account for $signer"
in_cluster "curl -sf -X PUT -H 'Authorization: Bearer $admin' -H 'Content-Type: application/json' -d '{\"type\":\"password\",\"value\":\"smoke-persona\",\"temporary\":false}' $kc/admin/realms/insys/users/$persona_id/reset-password"
in_cluster "curl -sf -X PUT -H 'Authorization: Bearer $admin' -H 'Content-Type: application/json' -d '{\"requiredActions\":[],\"emailVerified\":true}' $kc/admin/realms/insys/users/$persona_id"
persona=$(in_cluster "curl -sf -d grant_type=password -d client_id=insys-web -d username=$signer -d password=smoke-persona -d 'scope=openid insys-api' $kc/realms/insys/protocol/openid-connect/token | jq -r .access_token")
[ -n "$persona" ] && [ "$persona" != null ] || fail "$signer got no token from Keycloak"
me=$(in_cluster "curl -sS --fail-with-body -H 'Authorization: Bearer $persona' $api/identity/me") || fail "$signer couldn't read /identity/me: $me"
[ "$(jq -r .tenant.tenantCode <<<"$me")" = OFTZA ] || fail "$signer isn't in OFTZA: $(jq -c .tenant <<<"$me")"
echo "$signer signed in through Keycloak and read /identity/me in OFTZA ($(jq -r .tenant.status <<<"$me"))."
[ -z "${GITHUB_STEP_SUMMARY:-}" ] || echo "Smoke: $signer signed in through Keycloak and read /identity/me in OFTZA." >> "$GITHUB_STEP_SUMMARY"

echo "== The console's login theme (BR-168): the chart's own, then mounted as the Preview's keycloak-theme mounts it, adopted and removed"
# The chart as committed carries the approved theme and its realm names it: the sign-in page must come with the theme's own stylesheet,
# byte for byte the approved file. Then a stand-in theme (deploy/helm/tests/keycloak-theme) in a copy of the chart, never in the chart itself:
# deploy/keycloak/theme-mount.sh applies its ConfigMap as the chart renders it and patches Keycloak's Deployment, as the Preview's action
# does; twice, the second changing nothing. An upgrade with that chart must adopt the ConfigMap and leave Keycloak running as it is (the same
# volume, mount and checksum), and an upgrade with a chart without theme files (as before the sync) must remove both.
page=$(in_cluster "curl -s '$login'")
css=$(grep -o "/resources/[^/\"]*/login/insys/css/insys.css" <<<"$page" | sed -n 1p)
[ -n "$css" ] || fail "the chart's sign-in page isn't served with the login theme insys"
served=$(in_cluster "curl -sf '$kc$css' | sha256sum | cut -d' ' -f1")
grep -qx "$served  keycloak-theme/insys/login/resources/css/insys.css" "$here/../helm/insys/files/keycloak-theme/insys.sha256" \
  || fail "Keycloak's insys.css isn't the approved file (sha256 $served)"
echo "The chart's own theme is served: the sign-in page's insys.css is the approved file, byte for byte."
themed=$(mktemp -d)
cp -r "$here/../helm/insys" "$themed/insys"
rm -rf "$themed/insys/files/keycloak-theme" && mkdir -p "$themed/insys/files/keycloak-theme"
cp -r "$here/../helm/tests/keycloak-theme/insys" "$themed/insys/files/keycloak-theme/insys"
values=(-f "$here/values.yaml" -f "$here/values-seed.yaml")   # the release's settings since the seed's upgrade
helm template insys "$themed/insys" "${values[@]}" --show-only templates/keycloak-theme.yaml > "$themed/keycloak-theme.yaml"
theme_sum=$(helm template insys "$themed/insys" "${values[@]}" --show-only templates/keycloak.yaml | sed -n 's/^ *checksum\/keycloak-theme: \([0-9a-f]\{64\}\)$/\1/p')
[ -n "$theme_sum" ] || fail "the themed chart rendered no checksum/keycloak-theme"
"$here/../keycloak/theme-mount.sh" insys "$themed/keycloak-theme.yaml" "$theme_sum"
again=$("$here/../keycloak/theme-mount.sh" insys "$themed/keycloak-theme.yaml" "$theme_sum")
grep -q "already mounted as it is; unchanged" <<<"$again" || fail "a second mount changed Keycloak's Deployment: $again"
printf '%s' smoke-admin | "$here/../keycloak/realm-login-theme.sh" insys insys
[ "$(printf '%s' smoke-admin | "$here/../keycloak/realm-login-theme.sh" insys --show)" = insys ] || fail "the realm's login theme isn't insys"
theme_served() { # the theme the sign-in page's stylesheets must come from
  local page
  page=$(in_cluster "curl -s '$login'")
  grep -q "/resources/[^/\"]*/login/$1/css/" <<<"$page" || fail "the sign-in page isn't served with the login theme $1"
}
theme_served insys
page=$(in_cluster "curl -s '$login'")
css=$(grep -o "/resources/[^/\"]*/login/insys/css/insys.css" <<<"$page" | sed -n 1p)
stylesheet=$(in_cluster "curl -sf '$kc$css'") || fail "Keycloak didn't serve the theme's stylesheet ($css)"
grep -q "stand-in for the console's login theme" <<<"$stylesheet" || fail "Keycloak's stylesheet $css isn't the theme's own, from the mounted ConfigMap"
echo "The stand-in theme is mounted and served: its own stylesheet, from the ConfigMap's items."

# Whether the upgrade changes Keycloak's pod template (and so restarts it) is recorded, with what changed, not failed on: a restart at a later
# upgrade is harmless. What must hold is that the upgrade adopts the ConfigMap and leaves exactly one theme volume and mount, still served.
echo "helm: $(helm version --short)"
generation=$(kubectl get deploy insys-keycloak -o jsonpath='{.metadata.generation}')
kubectl get deploy insys-keycloak -o json | jq -S '.spec.template' > "$themed/template-before.json"
helm upgrade insys "$themed/insys" "${values[@]}"
kubectl wait --for=condition=complete "job/insys-migrate-$(helm status insys -o json | jq -r .version)" --timeout=600s
kubectl rollout status deploy/insys-keycloak --timeout=600s
manifest=$(helm get manifest insys)
grep -qx '  name: insys-keycloak-theme' <<<"$manifest" || fail "the chart's upgrade didn't adopt the theme's ConfigMap"
kubectl get deploy insys-keycloak -o json | jq -S '.spec.template' > "$themed/template-after.json"
if [ "$(kubectl get deploy insys-keycloak -o jsonpath='{.metadata.generation}')" = "$generation" ]; then
  echo "The chart's upgrade left Keycloak's Deployment as the mount made it: no restart."
  adopted="with no restart"
else
  echo "The chart's upgrade changed Keycloak's pod template, so Keycloak restarted. What changed (before, after):"
  diff "$themed/template-before.json" "$themed/template-after.json" | cut -c1-200 | head -n 40 || true
  adopted="with a restart (its pod template changed: see the log)"
fi
[ "$(kubectl get deploy insys-keycloak -o json | jq '[.spec.template.spec.containers[0].volumeMounts[] | select(.mountPath == "/opt/keycloak/themes/insys")] | length')" = 1 ] \
  || fail "after the chart's upgrade, Keycloak hasn't exactly one theme mount"
[ "$(kubectl get deploy insys-keycloak -o json | jq '[.spec.template.spec.volumes[] | select(.name == "keycloak-theme")] | length')" = 1 ] \
  || fail "after the chart's upgrade, Keycloak hasn't exactly one theme volume"
theme_served insys
echo "The chart's upgrade adopted the theme's ConfigMap, $adopted; one theme volume and mount, still served."

printf '%s' smoke-admin | "$here/../keycloak/realm-login-theme.sh" insys --default
theme_served keycloak.v2
cp -r "$here/../helm/insys" "$themed/bare"
rm -rf "$themed/bare/files/keycloak-theme"
jq 'del(.loginTheme)' "$here/../helm/insys/files/insys-realm.json" > "$themed/bare/files/insys-realm.json"
helm upgrade insys "$themed/bare" "${values[@]}"
kubectl wait --for=condition=complete "job/insys-migrate-$(helm status insys -o json | jq -r .version)" --timeout=600s
kubectl rollout status deploy/insys-keycloak --timeout=600s
! kubectl get configmap insys-keycloak-theme > /dev/null 2>&1 || fail "the chart without theme files left the theme's ConfigMap"
[ "$(kubectl get deploy insys-keycloak -o json | jq '[.spec.template.spec.volumes[] | select(.name == "keycloak-theme")] | length')" = 0 ] \
  || fail "the chart without theme files left Keycloak's theme volume"
theme_served keycloak.v2
echo "The chart without theme files removed the ConfigMap and the mount; the sign-in page is Keycloak's own."
[ -z "${GITHUB_STEP_SUMMARY:-}" ] || echo "Smoke: the login theme (a stand-in) mounted as the Preview's keycloak-theme does, adopted by the chart's upgrade $adopted, and removed by the chart without it." >> "$GITHUB_STEP_SUMMARY"

trap - ERR
echo "SMOKE PASSED"
