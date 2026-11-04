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
# Names the chart's Keycloak realm for its sign-in page: displayName (the page's title) and displayNameHtml (its heading), through the admin API
# on a port-forward. The chart's realm import never touches a realm that already exists, so this is what names one. Idempotent: a realm that
# already has the name is left alone. The master admin's password goes in on stdin, never on a command line or in a log.
#
#   realm-name.sh <release> <realm> <name>      prints whether it set the name or found it set.
set -euo pipefail
release=${1:?the release name}
realm=${2:?the realm}
name=${3:?the name}
port=${KEYCLOAK_LOCAL_PORT:-18089}
IFS= read -r password || [ -n "$password" ]   # with or without a final newline

kubectl port-forward "svc/$release-keycloak" "$port:8080" >/dev/null 2>&1 &
forward=$!
trap 'kill $forward 2>/dev/null || true' EXIT
for _ in $(seq 1 30); do curl -s -o /dev/null "http://127.0.0.1:$port/realms/master" && break; sleep 1; done
token=$(printf '%s' "$password" | curl -sS --fail-with-body -d grant_type=password -d client_id=admin-cli -d username=admin \
  --data-urlencode "password@-" "http://127.0.0.1:$port/realms/master/protocol/openid-connect/token" | jq -r .access_token)
kc() { # method; the body, if any, on stdin. The token goes in a header file, not on the command line.
  curl -sS --fail-with-body -X "$1" -H @<(printf 'Authorization: Bearer %s\n' "$token") -H 'Content-Type: application/json' \
    ${2:+--data-binary @-} "http://127.0.0.1:$port/admin/realms/$realm"
}

wanted=$(jq -nc --arg n "$name" '{displayName: $n, displayNameHtml: ($n | @html)}')
if [ "$(kc GET | jq -c '{displayName, displayNameHtml}')" = "$wanted" ]; then
  echo "The realm $realm is already named \"$name\"."
else
  kc PUT body <<<"$wanted"
  echo "The realm $realm is now named \"$name\"."
fi
