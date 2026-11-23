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
# The realm's login theme (BR-168), through the admin API on a port-forward to the release's Keycloak, as realm-name.sh names the realm: the
# chart's realm import never touches a realm that already exists, so this is what sets one's theme. Idempotent: a realm that already has the
# theme is left alone. A theme is set only if Keycloak has it (the admin API's server info), so a theme that isn't mounted is refused, not set.
# The master admin's password goes in on stdin, never on a command line or in a log; the token never leaves this script.
#
#   realm-login-theme.sh <realm> --show        prints the realm's login theme, or (default) when it has none (Keycloak's own, keycloak.v2)
#   realm-login-theme.sh <realm> <theme>       sets it, such as insys, and prints what it was
#   realm-login-theme.sh <realm> --default     clears it, back to Keycloak's default
#   realm-login-theme.sh <realm> --version     prints the version Keycloak itself reports (the admin API's server info), such as 26.7.5
#
# KEYCLOAK_RELEASE names the release (insys by default). KEYCLOAK_URL, if set, is Keycloak's address instead of a port-forward (a local test).
set -euo pipefail
realm=${1:?the realm}
want=${2:?the theme, --default, --show or --version}
case "$want" in
  --show | --default | --version) ;;
  *) [[ $want =~ ^[A-Za-z0-9._-]+$ ]] || { echo "realm-login-theme: \"$want\" isn't a theme name" >&2; exit 1; } ;;
esac
IFS= read -r password || [ -n "$password" ]   # with or without a final newline

if [ -n "${KEYCLOAK_URL:-}" ]; then
  base=${KEYCLOAK_URL%/}
else
  port=${KEYCLOAK_LOCAL_PORT:-18090}
  kubectl port-forward "svc/${KEYCLOAK_RELEASE:-insys}-keycloak" "$port:8080" >/dev/null 2>&1 &
  forward=$!
  trap 'kill $forward 2>/dev/null || true' EXIT
  base=http://127.0.0.1:$port
  for _ in $(seq 1 30); do curl -s -o /dev/null "$base/realms/master" && break; sleep 1; done
fi
token=$(printf '%s' "$password" | curl -sS --fail-with-body -d grant_type=password -d client_id=admin-cli -d username=admin \
  --data-urlencode "password@-" "$base/realms/master/protocol/openid-connect/token" | jq -r .access_token)
kc() { # method, path under /admin; the body, if any, on stdin. The token goes in a header file, not on the command line.
  curl -sS --fail-with-body -X "$1" -H @<(printf 'Authorization: Bearer %s\n' "$token") -H 'Content-Type: application/json' \
    ${3:+--data-binary @-} "$base/admin$2"
}
shown() { if [ -n "$1" ]; then printf '%s' "$1"; else printf '(default)'; fi; }

if [ "$want" = --version ]; then
  kc GET /serverinfo | jq -r '.systemInfo.version'
  exit 0
fi
current=$(kc GET "/realms/$realm" | jq -r '.loginTheme // ""')
if [ "$want" = --show ]; then
  shown "$current"; echo
  exit 0
fi
[ "$want" = --default ] && want=""
if [ -n "$want" ] && ! kc GET /serverinfo | jq -e --arg t "$want" '[.themes.login[].name] | index($t)' >/dev/null; then
  echo "realm-login-theme: Keycloak has no login theme $want (is it mounted?); the realm's stays $(shown "$current")." >&2
  exit 1
fi
if [ "$current" = "$want" ]; then
  echo "The realm $realm's login theme is already $(shown "$want")."
  exit 0
fi
jq -nc --arg t "$want" '{loginTheme: $t}' | kc PUT "/realms/$realm" body
now=$(kc GET "/realms/$realm" | jq -r '.loginTheme // ""')
[ "$now" = "$want" ] || { echo "realm-login-theme: Keycloak took the change but the realm's login theme reads $(shown "$now")" >&2; exit 1; }
echo "The realm $realm's login theme is now $(shown "$want") (was $(shown "$current"))."
