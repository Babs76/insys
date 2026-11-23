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
# The Preview's login theme check (BR-168) signs in as a temporary Keycloak-only user, as the sample accounts' passwords are not on the server
# (the seed gives them only to Ade's encrypted file). This makes and removes that user, through the admin API on a port-forward to the release's
# Keycloak. It is not an INSYS user: INSYS has no record of it, and it holds no role. The realm's user profile requires tenant_id from an
# administrator, so it carries the nil UUID, which is no tenant. It must set up a one-time code at its first sign-in (CONFIGURE_TOTP).
# Only names starting theme-check- are made or removed, so this can never touch anyone else's account.
#
#   theme-check-user.sh <realm> create <name>    stdin: the master admin's password, then the user's password, one per line. Any
#                                                earlier theme-check- user goes first, so a rerun or a run cut off leaves none behind.
#   theme-check-user.sh <realm> delete <name>    stdin: the master admin's password. Removes its one-time code credential, then the
#                                                user, and prints whether it removed them.
#
# Neither password is ever on a command line or in a log. KEYCLOAK_RELEASE names the release (insys by default); KEYCLOAK_URL, if set, is
# Keycloak's address instead of a port-forward (a local test).
set -euo pipefail
realm=${1:?the realm}
action=${2:?create or delete}
name=${3:?the user name, theme-check-...}
[[ $name =~ ^theme-check-[A-Za-z0-9-]+$ ]] || { echo "theme-check-user: only a theme-check- user, not \"$name\"" >&2; exit 1; }
IFS= read -r password || [ -n "$password" ]
user_password=""
if [ "$action" = create ]; then
  IFS= read -r user_password || [ -n "$user_password" ]
  [ "${#user_password}" -ge 16 ] || { echo "theme-check-user: the user's password must come second on stdin, of 16 characters or more" >&2; exit 1; }
fi

if [ -n "${KEYCLOAK_URL:-}" ]; then
  base=${KEYCLOAK_URL%/}
else
  port=${KEYCLOAK_LOCAL_PORT:-18091}
  kubectl port-forward "svc/${KEYCLOAK_RELEASE:-insys}-keycloak" "$port:8080" >/dev/null 2>&1 &
  forward=$!
  trap 'kill $forward 2>/dev/null || true' EXIT
  base=http://127.0.0.1:$port
  for _ in $(seq 1 30); do curl -s -o /dev/null "$base/realms/master" && break; sleep 1; done
fi
token=$(printf '%s' "$password" | curl -sS --fail-with-body -d grant_type=password -d client_id=admin-cli -d username=admin \
  --data-urlencode "password@-" "$base/realms/master/protocol/openid-connect/token" | jq -r .access_token)
kc() { # method, path under the realm's admin API; the body, if any, on stdin. The token goes in a header file, not on the command line.
  curl -sS --fail-with-body -X "$1" -H @<(printf 'Authorization: Bearer %s\n' "$token") -H 'Content-Type: application/json' \
    ${3:+--data-binary @-} "$base/admin/realms/$realm$2"
}
# A user's one-time code credentials removed first (its authenticator), each checked gone, then the user (Ade, 22 November 2026).
remove() { # the user's id
  local otp
  for otp in $(kc GET "/users/$1/credentials" | jq -r '.[] | select(.type == "otp") | .id'); do kc DELETE "/users/$1/credentials/$otp"; done
  [ "$(kc GET "/users/$1/credentials" | jq '[.[] | select(.type == "otp")] | length')" = 0 ] || { echo "theme-check-user: its one-time code credential is still there" >&2; exit 1; }
  kc DELETE "/users/$1"
}
# The ids of theme-check- users: this name exactly, or with "all", every one (the search matches anywhere in the name, so it is narrowed here).
ids() {
  kc GET "/users?briefRepresentation=true&max=100&username=theme-check-" \
    | jq -r --arg n "$1" '.[] | select(.username | startswith("theme-check-")) | select($n == "all" or .username == $n) | .id'
}

case "$action" in
  create)
    for id in $(ids all); do remove "$id"; echo "Removed an earlier theme-check user and its one-time code."; done
    # The password goes into the body from stdin (jq -R), not through an argument.
    printf '%s' "$user_password" | jq -Rsc --arg n "$name" '{username: $n, enabled: true, firstName: "Theme", lastName: "Check",
        attributes: {tenant_id: ["00000000-0000-0000-0000-000000000000"]}, requiredActions: ["CONFIGURE_TOTP"],
        credentials: [{type: "password", value: ., temporary: false}]}' \
      | kc POST /users body >/dev/null
    [ -n "$(ids "$name")" ] || { echo "theme-check-user: Keycloak didn't make $name" >&2; exit 1; }
    echo "Made the temporary user $name, with a one-time code to set up at its first sign-in."
    ;;
  delete)
    found=$(ids "$name")
    for id in $found; do remove "$id"; done
    [ -z "$(ids "$name")" ] || { echo "theme-check-user: $name is still there" >&2; exit 1; }
    if [ -n "$found" ]; then echo "Removed the temporary user $name: its one-time code credential, then the user."; else echo "There was no user $name to remove."; fi
    ;;
  *)
    echo "theme-check-user: create or delete, not $action" >&2; exit 1 ;;
esac
