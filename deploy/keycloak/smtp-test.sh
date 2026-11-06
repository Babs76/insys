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
# Sends Keycloak's SMTP test email once, through the realm's own SMTP settings, to an address typed at a silent prompt (Ade, 6 November).
# Keycloak sends its test only to the signed-in admin's own address (RealmAdminResource.testSMTPConnection), so the address is put on the
# master realm's admin account for the test and removed straight after, whatever happens, and the removal is checked. The address is never
# on a command line, in a file or in the output: it goes to Keycloak on stdin, and anything Keycloak says back has it masked.
#
# On the server, as root, after the workflow's open-ssh:   /root/insys/keycloak/smtp-test.sh     (the address at the prompt)
set -euo pipefail
release=${1:-insys}
realm=${2:-insys}
port=${KEYCLOAK_LOCAL_PORT:-18086}
IFS= read -rsp "Address for the test email (it isn't shown): " to; echo
[[ $to == *@*.* ]] || { echo "That isn't an email address."; exit 1; }
password=$(kubectl get secret "$release-secrets" -o jsonpath='{.data.keycloakAdminPassword}' | base64 -d)

kubectl port-forward "svc/$release-keycloak" "$port:8080" >/dev/null 2>&1 &
forward=$!
for _ in $(seq 1 30); do curl -s -o /dev/null "http://127.0.0.1:$port/realms/master" && break; sleep 1; done
token=$(printf '%s' "$password" | curl -sS --fail-with-body -d grant_type=password -d client_id=admin-cli -d username=admin \
  --data-urlencode "password@-" "http://127.0.0.1:$port/realms/master/protocol/openid-connect/token" | jq -r .access_token)
unset password
kc() { # method, path under /admin/realms; the body, if any, on stdin. The token goes in a header file.
  curl -sS -X "$1" -H @<(printf 'Authorization: Bearer %s\n' "$token") -H 'Content-Type: application/json' ${3:+--data-binary @-} \
    -w '\n%{http_code}' "http://127.0.0.1:$port/admin/realms/$2"
}
admin=$(kc GET "master/users?username=admin&exact=true" | sed '$d' | jq -r '.[0].id')
before=$(kc GET "master/users/$admin" | sed '$d' | jq -r '.email // ""')

put_email() { printf '%s' "$1" | jq -Rc '{email: .}' | kc PUT "master/users/$admin" body | tail -n 1; }
restore() { # whatever happened: the admin's address as it was before the test (none, normally), and checked
  put_email "$before" >/dev/null || true
  now=$(kc GET "master/users/$admin" | sed '$d' | jq -r '.email // ""')
  if [ "$now" = "$before" ]; then echo "The test address is removed from the admin account."; else echo "WARNING: the admin account's address wasn't put back: check it in Keycloak."; fi
  kill "$forward" 2>/dev/null || true
}
trap restore EXIT

[ "$(put_email "$to")" = 204 ] || { echo "Keycloak refused the address for the admin account."; exit 1; }
answer=$(kc GET "$realm" | sed '$d' | jq -c '.smtpServer' | kc POST "$realm/testSMTPConnection" body)
code=$(tail -n 1 <<<"$answer")
said=$(sed '$d' <<<"$answer" | jq -r '.errorMessage // empty' 2>/dev/null | sed "s/$(printf '%s' "$to" | sed 's/[][\.*^$/]/\\&/g')/(the address)/g")
unset to
if [ "$code" = 204 ]; then
  echo "Keycloak sent the test email through $(kc GET "$realm" | sed '$d' | jq -r .smtpServer.host): check the inbox (and spam)."
else
  echo "Keycloak couldn't send it (HTTP $code): ${said:-no message}. If Google's relay hasn't applied yet, wait and try again later."
fi
