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

kubectl run smoke --image=alpine:3.20 --restart=Never --command -- sleep 3600
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

echo "== The OFTZA seed through Keycloak, as the Preview runs it (batch 6, Ade 19 November 2026)"
# The release is upgraded to the Preview's settings (values-seed.yaml: the Preview environment, declared non-production, the operator tenant
# and its two administrators bootstrapped), and the seed runs from the runner through port-forwards, as preview.sh's seed() does through its
# tunnels: --keycloak-url, the realm's insys-seed client enabled for the run only, every persona signing in through Keycloak. The workflow
# builds the seed first (SEED_DLL). Its generic accounts' passwords come back on its stdout only, masked, and are used once below.
seed_dll=${SEED_DLL:?the workflow builds tools/Insys.Seed and gives its dll}
helm upgrade insys "$here/../helm/insys" -f "$here/values.yaml" -f "$here/values-seed.yaml"
kubectl wait --for=condition=complete job/insys-migrate-2 --timeout=600s
kubectl rollout status deploy/insys-api --timeout=600s
forward() { # service local-port: kept up for the seed's whole run, restarted if it drops
  while true; do kubectl port-forward "svc/$1" "$2:8080" > /dev/null 2>&1 || true; sleep 1; done
}
forward insys-api 18080 & forward_api=$!
forward insys-keycloak 18081 & forward_kc=$!
stop_forwards() { kill "$forward_api" "$forward_kc" 2> /dev/null || true; pkill -f '^kubectl port-forward svc/insys-' || true; }
for url in http://127.0.0.1:18080/health/ready http://127.0.0.1:18081/realms/insys; do
  for _ in $(seq 1 60); do [ "$(curl -s -o /dev/null -w '%{http_code}' "$url")" = 200 ] && continue 2; sleep 2; done
  fail "through the port-forward, $url didn't answer 200"
done
started=$(date +%s)
accounts=$(INSYS_SEED_KEYCLOAK_ADMIN_PASSWORD=smoke-admin dotnet "$seed_dll" --api http://127.0.0.1:18080 --keycloak-url http://127.0.0.1:18081 \
  --keycloak-admin admin --operator-tenant 0199f000-0000-7000-8000-000000000001 --platform-1 insys-admin --platform-2 insys-admin-2 --accounts true) \
  || { stop_forwards; kubectl logs deploy/insys-api --tail=80 || true; fail "the seed failed against the deployed API"; }
seconds=$(( $(date +%s) - started ))
echo "The seed ran in ${seconds}s (full seed, every persona through Keycloak)."
while read -r password; do echo "::add-mask::$password"; done < <(jq -r '.accounts[].password' <<<"$accounts")
[ -z "${GITHUB_STEP_SUMMARY:-}" ] || echo "Smoke: the OFTZA seed ran against the deployed API through Keycloak in ${seconds}s." >> "$GITHUB_STEP_SUMMARY"

# The seed disabled its client again.
admin=$(in_cluster "curl -sf -d grant_type=password -d client_id=admin-cli -d username=admin -d password=smoke-admin $kc/realms/master/protocol/openid-connect/token | jq -r .access_token")
[ "$(in_cluster "curl -sf -H 'Authorization: Bearer $admin' '$kc/admin/realms/insys/clients?clientId=insys-seed' | jq -r '.[0].enabled'")" = false ] \
  || fail "the seed left its Keycloak client insys-seed enabled"

# One seeded persona, the auditor, signs in through the realm (insys-web, which this cluster lets take a password) and reads as itself.
sign_in=$(jq -r '.accounts[] | select(.role == "AUDITOR") | .signIn' <<<"$accounts" | head -n 1)
password=$(jq -r --arg s "$sign_in" '.accounts[] | select(.signIn == $s) | .password' <<<"$accounts")
persona=$(printf '%s' "$password" | curl -sf -d grant_type=password -d client_id=insys-web -d "username=$sign_in" --data-urlencode password@- \
  -d 'scope=openid insys-api' http://127.0.0.1:18081/realms/insys/protocol/openid-connect/token | jq -r .access_token)
[ -n "$persona" ] && [ "$persona" != null ] || { stop_forwards; fail "the seeded persona $sign_in got no token from Keycloak"; }
me=$(curl -sf -H @<(printf 'Authorization: Bearer %s\n' "$persona") http://127.0.0.1:18080/api/v1/identity/me) || { stop_forwards; fail "the seeded persona couldn't read /identity/me"; }
[ "$(jq -r .tenant.tenantCode <<<"$me")" = OFTZA ] || { stop_forwards; fail "the seeded persona isn't in OFTZA: $(jq -c .tenant <<<"$me")"; }
echo "The seeded persona $sign_in signed in through Keycloak and read /identity/me in OFTZA."
stop_forwards

trap - ERR
echo "SMOKE PASSED"
