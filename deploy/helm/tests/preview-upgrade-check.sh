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
# The Preview's upgrade check (deploy/preview/preview.sh: upgrade_check, in upgrade after chart and as the read-only upgrade-check action;
# Ade, 26 November 2026), against a stubbed server and tool: a clear report passes; a report listing approval types fails the run with them
# by tenant; a tool that couldn't read the API fails too, as an unread list isn't an empty one. No network, no server, no .NET.
set -euo pipefail
repo=$(cd "$(dirname "$0")/../../.." && pwd)
work=$(mktemp -d)
trap 'rm -rf "$work"' EXIT

sed -n '/^tunnel()/,/^}/p;/^upgrade_check()/,/^}/p;/^mask() *{/p' "$repo/deploy/preview/preview.sh" > "$work/functions.sh"
# preview.sh's own mask, not a stub: no line of a run's output but the mask's may hold Keycloak's admin password (preview-masking.sh).
# shellcheck source=deploy/helm/tests/secret-output.sh disable=SC1091 # sourced at run time
. "$repo/deploy/helm/tests/secret-output.sh"
for f in tunnel upgrade_check; do grep -q "^$f()" "$work/functions.sh" || { echo "$f not found in preview.sh"; exit 1; }; done
# The upgrade runs the check after the chart (the new version answers) and before nothing that deploys; the standalone action deploys nothing.
# shellcheck disable=SC2016 # the lines of preview.sh, as written
grep -q '^    ssh_key; open_ssh "${RUNNER_IP:?}"; wait_for_ssh; running_release; backup_now; os_policy; cluster; images; pull_secret; chart; upgrade_counts; upgrade_check; check_tls$' "$repo/deploy/preview/preview.sh" \
  || { echo "FAIL: upgrade doesn't run the release check and backup first, and upgrade_check after chart"; exit 1; }
# shellcheck disable=SC2016 # the lines of preview.sh, as written
grep -A1 '^  upgrade-check)' "$repo/deploy/preview/preview.sh" | grep -q '^    ssh_key; open_ssh "${RUNNER_IP:?}"; wait_for_ssh; upgrade_check$' \
  || { echo "FAIL: the upgrade-check action does more than the check"; exit 1; }
if ! grep -q "fromJSON('\[\"upgrade-check\", " "$repo/.github/workflows/preview.yml" || ! grep -q 'options: \[.* upgrade-check,' "$repo/.github/workflows/preview.yml"; then
  echo "FAIL: preview.yml doesn't offer upgrade-check with its confirm word"; exit 1
fi
secret=stub-admin-value

# One scenario, in a subshell: $1 its name; the stubs read the others from the environment.
#   REPORT   what the tool prints on stdout
#   CODE     the tool's exit code
# shellcheck disable=SC2034,SC2329   # the stubs are used by the functions sourced from preview.sh
run() (
  export RUNNER_TEMP=$work/$1 GITHUB_STEP_SUMMARY=$work/$1/summary
  mkdir -p "$RUNNER_TEMP"; : > "$RUNNER_TEMP/log"; : > "$GITHUB_STEP_SUMMARY"
  key=$RUNNER_TEMP/key operator_tenant=0199f000-0000-7000-8000-000000000001
  fail() { echo "::error::$*" >&2; exit 1; }
  summary() { printf '%s\n\n' "$*" >> "$GITHUB_STEP_SUMMARY"; }
  exec 9>&1   # the step's own output, as preview.sh opens it
  address() { echo 192.0.2.10; }
  forwards() { echo "forwards" >> "$RUNNER_TEMP/log"; }
  ssh() { echo "ssh $*" >> "$RUNNER_TEMP/log"; }
  curl() { printf 200; }
  on_server() {
    local cmd="$*"
    echo "server: $cmd" >> "$RUNNER_TEMP/log"
    case "$cmd" in
      "kubectl get secret insys-secrets -o jsonpath='{.data.keycloakAdminPassword}' | base64 -d") printf '%s' "$secret" ;;
      "pkill -f '^kubectl port-forward svc/insys-' || true") ;;
      *) echo "unexpected server command: $cmd" >> "$RUNNER_TEMP/unexpected"; return 1 ;;
    esac
  }
  dotnet() {
    if [ "$1" = build ]; then return 0; fi
    echo "dotnet $*" >> "$RUNNER_TEMP/tool"
    [ "${INSYS_SEED_KEYCLOAK_ADMIN_PASSWORD:-}" = "$secret" ] || echo "no admin password in the environment" >> "$RUNNER_TEMP/unexpected"
    printf '%s\n' "$REPORT"
    return "$CODE"
  }
  # shellcheck disable=SC1091
  . "$work/functions.sh"
  upgrade_check
)

pass=0
expect() { # name, wanted outcome (ok or fail), then the scenario's settings
  local name=$1 outcome=$2 got; shift 2
  if env "$@" bash -c "$(declare -f run); $(declare -p work repo secret); run $name" > "$work/$name.out" 2>&1; then got=ok; else got=fail; fi
  [ "$got" = "$outcome" ] || { echo "FAIL $name: $got, not $outcome"; cat "$work/$name.out"; exit 1; }
  [ ! -s "$work/$name/unexpected" ] || { echo "FAIL $name: unexpected"; cat "$work/$name/unexpected"; exit 1; }
  only_masks "$work/$name.out" "$secret" || { echo "FAIL $name: Keycloak's admin password printed outside its mask"; exit 1; }
  grep -qx "::add-mask::$secret" "$work/$name.out" || { echo "FAIL $name: Keycloak's admin password wasn't masked on a line of its own"; exit 1; }
  grep -q -- "--upgrade-check true .*--operator-tenant 0199f000-0000-7000-8000-000000000001 --platform-1 insys-admin$" "$work/$name/tool" \
    || { echo "FAIL $name: the tool wasn't run as the upgrade check"; cat "$work/$name/tool"; exit 1; }
  ! grep -q -- "$secret\|--accounts\|--platform-2" "$work/$name/tool" || { echo "FAIL $name: a password, accounts or a second administrator on the tool's command line"; exit 1; }
  grep -q "^server: pkill -f '^kubectl port-forward svc/insys-' || true$" "$work/$name/log" || { echo "FAIL $name: the forwards weren't stopped"; exit 1; }
  pass=$((pass + 1)); echo "PASS $name"
}

clear=$'OFTZA Active: none\nOLD Terminated: not checked (terminated)\nupgrade-check: clear: in each of the 1 tenants checked, every approval type it can raise has a role holding the authority and the function to decide it'
expect an-empty-list-passes ok REPORT="$clear" CODE=0
grep -q "Upgrade check:\*\* clear" "$work/an-empty-list-passes/summary" || { echo "FAIL: the summary doesn't say clear"; exit 1; }

listed=$'OFTZA Active: COMMISSION_CLAWBACK_INVOICE, CREDIT_NOTE, DEAD_LETTER_DISCARD, DISCLOSURE_SETTING_CHANGE, PAYOUT_RETRY\nPAUSED Suspended: none\nupgrade-check: listed: 1 of the 2 tenants checked have approval types no role can decide'
expect a-listed-type-fails-the-run fail REPORT="$listed" CODE=1
grep -q "::error::the upgrade check lists approval types no role can decide: OFTZA Active: COMMISSION_CLAWBACK_INVOICE, CREDIT_NOTE, DEAD_LETTER_DISCARD, DISCLOSURE_SETTING_CHANGE, PAYOUT_RETRY;$" \
  "$work/a-listed-type-fails-the-run.out" || { echo "FAIL: the error doesn't name the tenant and its types"; cat "$work/a-listed-type-fails-the-run.out"; exit 1; }
! grep -q "PAUSED" "$work/a-listed-type-fails-the-run/summary" || { echo "FAIL: a tenant with nothing listed is in the summary's list"; exit 1; }
grep -q "OFTZA Active: COMMISSION_CLAWBACK_INVOICE" "$work/a-listed-type-fails-the-run/summary" || { echo "FAIL: the summary doesn't list the types"; exit 1; }

expect an-api-error-fails-the-run fail REPORT="" CODE=3
grep -q "::error::the upgrade check couldn't read every tenant's readiness (exit 3" "$work/an-api-error-fails-the-run.out" || { echo "FAIL: no unread error"; exit 1; }

expect a-listed-exit-with-no-list-still-fails fail REPORT="upgrade-check: listed: 1 of the 1 tenants checked have approval types no role can decide" CODE=1

echo "$pass scenarios passed"
