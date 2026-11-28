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
# running API's Insys assemblies hold the release's texts (deploy/preview/api-fingerprint.py against the reference fingerprint). Anything
# else fails the run. No network, no server. deploy/helm/tests/preview-release-images.sh runs the same check on real images.
# shellcheck disable=SC2034,SC2329,SC2001 # the stubs and variables are read by preview.sh's functions, sourced at run time
set -euo pipefail
repo=$(cd "$(dirname "$0")/../../.." && pwd)
tmp=$(mktemp -d)
trap 'rm -rf "$tmp"' EXIT

awk '/^release_commit=/{on=1} on{print} on&&/^running_release\(\)/{body=1} body&&/^}/{exit}' "$repo/deploy/preview/preview.sh" > "$tmp/functions.sh"
sed -n '/^counts()/,/^}/p' "$repo/deploy/preview/preview.sh" >> "$tmp/functions.sh"
for f in running_release counts upgrade_counts api_assemblies api_matches_release; do
  grep -q "^$f()" "$tmp/functions.sh" || { echo "$f not found in preview.sh"; exit 1; }
done

# A fake checkout: deploy/preview (here, with the fingerprint tool) and db/migrations, with V0001 to V0003 in the release and V0004 after it.
fake="$tmp/repo"
mkdir -p "$fake/deploy/preview" "$fake/db/migrations"
cp "$repo/deploy/preview/api-fingerprint.py" "$fake/deploy/preview/"
for v in 1 2 3 4; do printf 'SELECT %s;\n' "$v" > "$fake/db/migrations/V000${v}__m$v.sql"; done
release_sums() { for v in 1 2 3; do echo "$v $(sha256sum "$fake/db/migrations/V000${v}__m$v.sql" | cut -d' ' -f1)"; done; }

# Fake assemblies: a file holding UTF-16 texts, as .NET stores string literals.
assembly() { # file, texts...
  local file=$1; shift
  : > "$file"
  for t in "$@"; do printf 'pad%s\0\0' "$t" | iconv -f UTF-8 -t UTF-16LE >> "$file"; done
}
build() { # directory, the Api's route text, then extra file names
  local dir=$1 route=$2; shift 2
  mkdir -p "$dir"
  assembly "$dir/Insys.Api.dll" "/api/v1/$route" "Release text"
  assembly "$dir/Insys.Party.Domain.dll" "PARTY_NOT_FOUND" "SELECT party"
  for extra in "$@"; do assembly "$dir/$extra" "Another assembly"; done
}
build "$tmp/release" parties
python3 -I "$repo/deploy/preview/api-fingerprint.py" "$tmp/release" > "$fake/deploy/preview/release-$(grep -m1 -o '^release_commit=[0-9a-f]\{7\}' "$repo/deploy/preview/preview.sh" | cut -d= -f2).fingerprint"
build "$tmp/another" parties/merge
build "$tmp/extra" parties Insys.New.dll
mkdir -p "$tmp/missing" && cp "$tmp/release/Insys.Api.dll" "$tmp/missing/"

run() { # applied migrations, then the directory the API's assemblies come from
  printf '%s\n' "$1" > "$tmp/applied"
  served=$2
  (
    set -euo pipefail
    here="$fake/deploy/preview"
    # shellcheck disable=SC1091 # extracted at run time
    . "$tmp/functions.sh"
    release_migration=3
    RUNNER_TEMP="$tmp/runner/$RANDOM"
    fail() { echo "FAILED: $*"; exit 1; }
    on_server() {
      case "$*" in
        *"tar -cf - Insys.*.dll"*) (cd "$served" && tar -cf - Insys.*.dll) ;;
        *psql*) if grep -q schema_migration; then cat "$tmp/applied"; else echo "parties 1"; fi ;;
        *) echo "  (what is running)" ;;
      esac
    }
    running_release
  ) 2>&1
}

good=$(release_sums)
failures=0
expect() { # name, pass or fail, applied, assemblies
  local out status=0
  out=$(run "$3" "$4") || status=$?
  if { [ "$2" = pass ] && [ $status -eq 0 ] && grep -q "The back end running is " <<<"$out"; } || { [ "$2" = fail ] && [ $status -ne 0 ] && grep -q '^FAILED: ' <<<"$out"; }; then
    echo "PASS $1$( [ "$2" = fail ] && grep '^FAILED: ' <<<"$out" | sed 's/^/: /')"
  else
    echo "FAIL $1 (status $status):"; sed 's/^/  /' <<<"$out"; failures=$((failures + 1))
  fi
}
mkdir -p "$tmp/runner"
expect the-release-passes pass "$good" "$tmp/release"
expect a-missing-migration-fails fail "$(head -n 2 <<<"$good")" "$tmp/release"
expect a-later-migration-fails fail "$good
4 $(sha256sum "$fake/db/migrations/V0004__m4.sql" | cut -d' ' -f1)" "$tmp/release"
expect a-changed-checksum-fails fail "$(sed '2s/ .*/ 0000000000000000000000000000000000000000000000000000000000000000/' <<<"$good")" "$tmp/release"
expect an-empty-database-fails fail "" "$tmp/release"
expect another-api-build-fails fail "$good" "$tmp/another"
expect an-extra-assembly-fails fail "$good" "$tmp/extra"
expect a-missing-assembly-fails fail "$good" "$tmp/missing"
[ "$failures" -eq 0 ] || { echo "$failures failure(s)"; exit 1; }
echo "8 scenarios passed"
