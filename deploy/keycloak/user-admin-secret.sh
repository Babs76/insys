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
# Gives the API the client secret of insys-user-admin, the realm's confidential client for creating users (Identity:Keycloak). The realm file
# names no secret, so Keycloak generates one when it imports the realm: this reads it through the admin API on a port-forward and writes it to
# the release's Secret (key keycloakUserAdminSecret), then restarts the API to read it. The secret goes from Keycloak's answer into the Secret
# on stdin only: never on a command line, in a log or in the chart's values. Idempotent: with the secret already in place, nothing changes.
# The master admin's password goes in on stdin.
#
#   user-admin-secret.sh <release> <realm>      prints whether it set the secret or found it set.
set -euo pipefail
release=${1:?the release name}
realm=${2:?the realm}
secret_name=${INSYS_SECRET_NAME:-$release-secrets}
port=${KEYCLOAK_LOCAL_PORT:-18088}
IFS= read -r password || [ -n "$password" ]   # with or without a final newline

kubectl port-forward "svc/$release-keycloak" "$port:8080" >/dev/null 2>&1 &
forward=$!
trap 'kill $forward 2>/dev/null || true' EXIT
for _ in $(seq 1 30); do curl -s -o /dev/null "http://127.0.0.1:$port/realms/master" && break; sleep 1; done
token=$(printf '%s' "$password" | curl -sS --fail-with-body -d grant_type=password -d client_id=admin-cli -d username=admin \
  --data-urlencode "password@-" "http://127.0.0.1:$port/realms/master/protocol/openid-connect/token" | jq -r .access_token)
kc() { # path under the realm's admin API. The token goes in a header file, not on the command line.
  curl -sS --fail-with-body -H @<(printf 'Authorization: Bearer %s\n' "$token") "http://127.0.0.1:$port/admin/realms/$realm/$1"
}

id=$(kc "clients?clientId=insys-user-admin" | jq -r '.[0].id // empty')
[ -n "$id" ] || { echo "The realm $realm has no client insys-user-admin." >&2; exit 1; }
secret=$(kc "clients/$id/client-secret" | jq -r '.value // empty')
[ -n "$secret" ] || { echo "Keycloak gave no secret for insys-user-admin." >&2; exit 1; }
current=$(kubectl get secret "$secret_name" -o jsonpath='{.data.keycloakUserAdminSecret}' | base64 -d 2>/dev/null || true)
if [ "$current" = "$secret" ]; then
  echo "The API already has insys-user-admin's secret."
  exit 0
fi
printf '%s' "$secret" | jq -Rsc '{stringData: {keycloakUserAdminSecret: .}}' | kubectl patch secret "$secret_name" --type merge --patch-file /dev/stdin >/dev/null
kubectl rollout restart "deploy/$release-api" >/dev/null
kubectl rollout status "deploy/$release-api" --timeout=600s >/dev/null
echo "The API now has insys-user-admin's secret, and has restarted to read it."
