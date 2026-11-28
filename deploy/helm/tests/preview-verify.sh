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
# The Preview's verify action (deploy/preview/preview.sh: verify; Ade, 28 November 2026), against a stubbed server, web and seed tool: it only
# opens SSH and reads; its SQL is read only; every item reports PASS or FAIL, and any FAIL fails the run. No network, no server, no database.
# shellcheck disable=SC2034,SC2329,SC2001 # the stubs and variables are read by preview.sh's functions, sourced at run time
set -euo pipefail
repo=$(cd "$(dirname "$0")/../../.." && pwd)
tmp=$(mktemp -d)
trap 'rm -rf "$tmp"' EXIT
p="$repo/deploy/preview/preview.sh"
failures=0
bad() { echo "FAIL: $*"; failures=$((failures + 1)); }

# shellcheck disable=SC2016 # the lines of preview.sh, as written
grep -A1 '^  verify)' "$p" | grep -q '^    ssh_key; open_ssh "${RUNNER_IP:?}"; wait_for_ssh; verify$' || bad "the verify action does more than open SSH and verify"
grep -q "fromJSON('\[\"upgrade-check\", \"read-authority\", \"verify\", " "$repo/.github/workflows/preview.yml" || bad "verify needs its confirm word"
grep -q 'options: \[.* verify,' "$repo/.github/workflows/preview.yml" || bad "verify isn't one of the workflow's actions"

sed -n '/^verify_authority_sql()/,/^}/p' "$p" > "$tmp/sql.sh"
# shellcheck disable=SC1091 # extracted at run time
sql=$(. "$tmp/sql.sh"; verify_authority_sql)
grep -qiwE 'INSERT|UPDATE|DELETE|ALTER|CREATE|DROP|TRUNCATE|GRANT|REVOKE|MERGE|COPY|CALL|DO|COMMIT|VACUUM|LOCK' <<<"$sql" && bad "the SQL has a statement that writes"
grep -qx 'SET default_transaction_read_only = on;' <<<"$sql" || bad "no SET default_transaction_read_only = on"
grep -qx 'BEGIN READ ONLY;' <<<"$sql" || bad "no BEGIN READ ONLY"
[ "$(tail -n 1 <<<"$sql")" = 'ROLLBACK;' ] || bad "the SQL doesn't end with ROLLBACK"

sed -n '/^verify_[a-z_]*()/,/^}/p;/^verify()/,/^}/p' "$p" > "$tmp/functions.sh"
for f in verify verify_console verify_app verify_theme verify_counts verify_authority verify_segregation; do grep -q "^$f()" "$tmp/functions.sh" || bad "$f not found"; done
mkdir -p "$tmp/repo/deploy/helm/insys" "$tmp/repo/deploy/preview"
printf 'console:\n  image: ghcr.io/babs76/insys-console-web@sha256:5672cf9e\n' > "$tmp/repo/deploy/helm/insys/values.yaml"

run() { # the scenario's settings: digest, theme, member client, conflicts
  (
    set -euo pipefail
    here="$tmp/repo/deploy/preview"; PREVIEW_DOMAIN=preview.example; operator_tenant=x
    digest=$1 theme=$2 member=$3 conflicts=$4
    fail() { echo "FAILED: $*"; exit 1; }
    mask() { :; }
    check_tls() { echo "both hosts: Let's Encrypt production, verified"; }
    counts() { echo "parties 30, users 99"; }
    tunnel() { :; }
    on_server() {
      case "$*" in
        *imageID*) echo "ghcr.io/babs76/insys-console-web@sha256:$digest " ;;
        *keycloakAdminPassword*) echo "not-a-password" ;;
        *psql*) cat >/dev/null; echo "authority in force today for PAYOUT_RETRY: TENANT_ADMIN (v1, seed)" ;;
        *) : ;;
      esac
    }
    curl() {
      local url="${*: -1}"
      case "$url" in
        *config.json) case "$url" in *member*) echo "{\"keycloak\":{\"clientId\":\"$member\"}}" ;; *) echo '{"keycloak":{"clientId":"insys-web"}}' ;; esac ;;
        *openid-connect/auth*) printf '<html><link href="/resources/x/login/%s/css/login.css">\nHTTP 200' "$theme" ;;
        *) printf '<html><body>app</body></html>\nHTTP 200' ;;
      esac
    }
    dotnet() { case "$1" in build) return 0 ;; *) if [ "$conflicts" = 0 ]; then echo "segregation-conflicts: OFTZA: none"; else echo "segregation-conflicts: OFTZA: 1: SOD_PAYOUT_BATCH (A and B)"; fi ;; esac; }
    # shellcheck disable=SC1091 # extracted at run time
    . "$tmp/functions.sh"
    verify
  ) 2>&1
}
expect() { # name, pass or fail, the item that must fail (or -), settings...
  local name=$1 want=$2 item=$3 out status=0; shift 3
  out=$(run "$@") || status=$?
  if [ "$want" = pass ]; then
    [ $status -eq 0 ] && grep -q '^Every check passed' <<<"$out" && ! grep -q '^FAIL' <<<"$out" && { echo "PASS $name"; return; }
  else
    [ $status -ne 0 ] && grep -q "^FAIL  $item:" <<<"$out" && [ "$(grep -c '^FAIL  ' <<<"$out")" = 1 ] && { echo "PASS $name"; return; }
  fi
  bad "$name (status $status):"; sed 's/^/  /' <<<"$out"
}
expect all-good pass - 5672cf9e insys member-portal 0
expect another-console-image fail "Console image" 078c7f59 insys member-portal 0
expect keycloaks-own-theme fail "Sign-in theme" 5672cf9e keycloak member-portal 0
expect member-portal-misconfigured fail "Member portal" 5672cf9e insys insys-web 0
expect a-segregation-conflict fail "Segregation conflicts" 5672cf9e insys member-portal 1
[ "$failures" -eq 0 ] || { echo "$failures failure(s)"; exit 1; }
echo "verify: every case as expected"
