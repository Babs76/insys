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
# Let's Encrypt's service on the Preview (Ade, 27 November 2026): a run with no ACME value fails instead of defaulting to staging. The real
# preview.sh's own start runs here, cut just after the check, with stub values for the variables it requires (no real token): no network,
# no server. It also checks that the workflow passes the input or the Environment's variable, with no
# default of its own, and that no staging default is left in the script.
set -euo pipefail
repo=$(cd "$(dirname "$0")/../../.." && pwd)
work=$(mktemp -d)
trap 'rm -rf "$work"' EXIT
failures=0
bad() { echo "FAIL: $*"; failures=$((failures + 1)); }

# Runs preview.sh's start: everything up to the action, with acme_service, then stops before any step.
start() {
  sed -n '1,/^acme_service$/p' "$repo/deploy/preview/preview.sh" > "$work/start.sh"
  grep -q '^acme_service$' "$work/start.sh" || { echo "acme_service isn't called in preview.sh"; exit 1; }
  echo 'echo STARTED' >> "$work/start.sh"
  env -i PATH="$PATH" RUNNER_TEMP="$work" HCLOUD_TOKEN=stub-not-a-token PREVIEW_DOMAIN=preview.example "$@" bash "$work/start.sh" check 2>&1
}

for value in staging production; do
  out=$(start PREVIEW_ACME="$value") || bad "PREVIEW_ACME=$value was refused: $out"
  grep -q '^STARTED$' <<<"$out" || bad "PREVIEW_ACME=$value didn't get past the check"
done
for case in unset empty prod STAGING; do
  case $case in
    unset) out=$(start) && bad "a run with PREVIEW_ACME unset started" ;;
    empty) out=$(start PREVIEW_ACME=) && bad "a run with PREVIEW_ACME empty started" ;;
    *) out=$(start PREVIEW_ACME="$case") && bad "a run with PREVIEW_ACME=$case started" ;;
  esac
  grep -q '^STARTED$' <<<"$out" && bad "PREVIEW_ACME $case got past the check"
  grep -q '::error::PREVIEW_ACME' <<<"$out" || bad "PREVIEW_ACME $case failed without saying why: $out"
done

# The check comes before any action runs: before the check action and before the case that runs every other action.
line() { grep -n -E "$1" "$repo/deploy/preview/preview.sh" | head -1 | cut -d: -f1; }
checked=$(line '^acme_service$')
# shellcheck disable=SC2016 # patterns matching the text of preview.sh
for start in '^if \[ "\$action" = check \]' '^case "\$action" in'; do
  at=$(line "$start")
  [ -n "$at" ] || { bad "preview.sh has no line $start"; continue; }
  [ "$checked" -lt "$at" ] || bad "acme_service runs after $start"
done

# shellcheck disable=SC2016 # the text of preview.sh, as written
grep -q -E 'PREVIEW_ACME:-[a-z]' "$repo/deploy/preview/preview.sh" && bad "preview.sh still has a default for PREVIEW_ACME"
yml="$repo/.github/workflows/preview.yml"
# shellcheck disable=SC2016 # the workflow's expression, as written
grep -q "^      PREVIEW_ACME: \${{ inputs.acme != 'environment' && inputs.acme || vars.PREVIEW_ACME }}" "$yml" \
  || bad "preview.yml doesn't pass the acme input or the Environment's PREVIEW_ACME"
grep -A4 '^      acme:' "$yml" | grep -q '^        options: \[environment, staging, production\]$' \
  || bad "the acme input's first (default) choice isn't environment"
grep -A4 '^      acme:' "$yml" | grep -q 'default:' && bad "the acme input has a default of its own"

[ "$failures" -eq 0 ] || { echo "$failures failure(s)"; exit 1; }
echo "PASS: a Preview run needs PREVIEW_ACME, staging or production; nothing defaults to staging"
