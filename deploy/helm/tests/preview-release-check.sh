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
# The Preview upgrade's release check (deploy/preview/preview.sh: running_release; Ade, 27 November 2026), against a stubbed server and a
# small fake repository: it passes only when the database's applied migrations are exactly the release's, with the same checksums, and the
# running API's Insys.Api.dll embeds the release's OpenAPI documents byte for byte. Anything else fails the run. No network, no server.
# shellcheck disable=SC2034,SC2329,SC2001 # the stubs and variables are read by preview.sh's functions, sourced at run time
set -euo pipefail
repo=$(cd "$(dirname "$0")/../../.." && pwd)
tmp=$(mktemp -d)
trap 'rm -rf "$tmp"' EXIT

sed -n '/^release_commit=/,/^}/p;/^counts()/,/^}/p' "$repo/deploy/preview/preview.sh" > "$tmp/functions.sh"
for f in running_release counts upgrade_counts; do grep -q "^$f()" "$tmp/functions.sh" || { echo "$f not found in preview.sh"; exit 1; }; done

# A fake checkout: deploy/preview (here) and db/migrations, with V0001 to V0003 in the release and V0004 after it.
fake="$tmp/repo"
mkdir -p "$fake/deploy/preview" "$fake/db/migrations"
for v in 1 2 3 4; do printf 'SELECT %s;\n' "$v" > "$fake/db/migrations/V000${v}__m$v.sql"; done
release_sums() { for v in 1 2 3; do echo "$v $(sha256sum "$fake/db/migrations/V000${v}__m$v.sql" | cut -d' ' -f1)"; done; }
for d in core static-data platform-operator; do printf '{"document":"%s","release":true}' "$d" > "$tmp/openapi-$d.json"; done

run() { # applied migrations, then the dll's content; prints the output, returns the status
  local applied=$1 dll=$2
  printf '%s' "$dll" > "$tmp/served.dll"
  printf '%s\n' "$applied" > "$tmp/applied"
  (
    set -euo pipefail
    # shellcheck disable=SC1091 # extracted at run time
    . "$tmp/functions.sh"
    release_migration=3
    here="$fake/deploy/preview"
    RUNNER_TEMP="$tmp/runner"
    fail() { echo "FAILED: $*"; exit 1; }
    on_server() {
      case "$*" in
        *"cat /app/Insys.Api.dll"*) cat "$tmp/served.dll" ;;
        *psql*) if grep -q schema_migration; then cat "$tmp/applied"; else echo "parties 1"; fi ;;
        *) echo "  (what is running)" ;;
      esac
    }
    git() { case "$*" in *" fetch "*) return 0 ;; *" show "*) cat "$tmp/openapi-$(sed 's/.*openapi-\(.*\)\.json/\1/' <<<"${*: -1}").json" ;; esac; }
    running_release
  ) 2>&1
}

docs=$(cat "$tmp"/openapi-core.json "$tmp"/openapi-static-data.json "$tmp"/openapi-platform-operator.json)
good=$(release_sums)
failures=0
expect() { # name, pass or fail, applied, dll
  local out status=0
  out=$(run "$3" "$4") || status=$?
  if { [ "$2" = pass ] && [ $status -eq 0 ] && grep -q "is ca1bf2b's" <<<"$out"; } || { [ "$2" = fail ] && [ $status -ne 0 ] && grep -q '^FAILED: ' <<<"$out"; }; then
    echo "PASS $1$( [ "$2" = fail ] && grep "^FAILED: " <<<"$out" | sed "s/^/: /")"
  else
    echo "FAIL $1 (status $status):"; sed 's/^/  /' <<<"$out"; failures=$((failures + 1))
  fi
}
mkdir -p "$tmp/runner"
expect the-release-passes pass "$good" "prefix${docs}suffix"
expect a-missing-migration-fails fail "$(head -n 2 <<<"$good")" "$docs"
expect a-later-migration-fails fail "$good
4 $(sha256sum "$fake/db/migrations/V0004__m4.sql" | cut -d' ' -f1)" "$docs"
expect a-changed-checksum-fails fail "$(sed '2s/ .*/ 0000000000000000000000000000000000000000000000000000000000000000/' <<<"$good")" "$docs"
expect another-api-build-fails fail "$good" "$(cat "$tmp"/openapi-core.json "$tmp"/openapi-static-data.json)"
expect an-empty-database-fails fail "" "$docs"
[ "$failures" -eq 0 ] || { echo "$failures failure(s)"; exit 1; }
echo "6 scenarios passed"
