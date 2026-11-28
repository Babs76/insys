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
# The Preview upgrade's API check on real images (Ade, 27 November 2026): the API image built from the release (release_commit), as the Preview built
# it (its Dockerfile, its .dockerignore, its tree), must match the reference fingerprint, and the image built from this checkout must not.
# Usage: preview-release-images.sh IMAGE   where IMAGE is this checkout's API image (CI's "The images build" step makes insys-api).
# Or:    preview-release-images.sh --directories RELEASE OTHER   the same check on two published API directories (no Docker).
# shellcheck disable=SC2034,SC2154 # here and release_commit are preview.sh's, sourced at run time
set -euo pipefail
repo=$(cd "$(dirname "$0")/../../.." && pwd)
tmp=$(mktemp -d)
trap 'rm -rf "$tmp"' EXIT
awk '/^release_commit=/{on=1} on{print} /^api_matches_release\(\)/{body=1} body&&/^}/{exit}' "$repo/deploy/preview/preview.sh" > "$tmp/functions.sh"
here="$repo/deploy/preview"
# shellcheck disable=SC1091 # extracted at run time
. "$tmp/functions.sh"

from_image() { # image, directory
  local id
  id=$(docker create "$1")
  mkdir -p "$2"
  docker cp "$id:/app/." "$2" >/dev/null
  docker rm "$id" >/dev/null
}

if [ "${1:-}" = --directories ]; then
  release=$2 other=$3
else
  : "${1:?the API image built from this checkout}"
  git -C "$repo" fetch -q --depth=1 origin "$release_commit"
  mkdir -p "$tmp/source"
  git -C "$repo" archive "$release_commit" | tar -x -C "$tmp/source"
  docker build -q -f "$tmp/source/deploy/docker/api.Dockerfile" -t insys-api:release-check "$tmp/source" >/dev/null
  from_image insys-api:release-check "$tmp/release"
  from_image "$1" "$tmp/other"
  release=$tmp/release other=$tmp/other
fi

echo "The API image built from $release_commit:"
api_matches_release "$release" || { echo "FAIL: $release_name's own image doesn't match the reference fingerprint"; exit 1; }
echo "The API image built from this checkout:"
# This checkout's image must differ only where the source does: with no change under src/ since the release, it is the same API.
if [ "${1:-}" != --directories ] && git -C "$repo" diff --quiet "$release_commit" HEAD -- src; then
  api_matches_release "$other" || { echo "FAIL: src/ is unchanged since $release_name, yet this checkout's image doesn't match it"; exit 1; }
  echo "PASS: the check passes on $release_name's API image, and on this checkout's, whose src/ is unchanged since it"
  exit 0
fi
if api_matches_release "$other"; then echo "FAIL: this checkout's image matches $release_name's, so the check can't tell them apart"; exit 1; fi
echo "PASS: the check passes on $release_name's API image and fails on this checkout's"
