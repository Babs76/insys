#!/usr/bin/env bash
# ============================================================================
# OPENFACTOR TECHNOLOGY GROUP
# CONFIDENTIAL AND PROPRIETARY SOFTWARE
# ============================================================================
# Copyright (c) 2026 OpenFactor Technology Group.
# All rights reserved.
# PRODUCT       : INSYS
# COMPONENT     : Tools
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
# The console's Keycloak login theme into the chart (BR-168): keycloak-theme/insys of Babs76/insys-console at exactly one commit, from git's
# objects (git archive, each file checked against its blob), never from a working tree, into deploy/helm/insys/files/keycloak-theme/insys/,
# with the commit recorded in deploy/helm/insys/files/keycloak-theme/insys.commit. The chart then renders the theme's ConfigMap and mount.
#
#   tools/sync-keycloak-theme.sh <console checkout or mirror> <commit>
#
# It refuses, changing nothing, unless the folder at that commit has login/theme.properties and holds only the expected kinds of file:
# FreeMarker templates (.ftl), properties (.properties: theme settings and message bundles), stylesheets (.css) and images (.png, .jpg,
# .jpeg, .gif, .svg, .webp, .ico). A script (.js, .mjs) is refused by name, as the theme has none of its own; so is anything else, a symbolic
# link or a submodule, a name a ConfigMap key can't hold, and a theme too big for one ConfigMap. It prints the files either way.
# The same commit then sets keycloak.loginTheme: insys in deploy/preview/values.yaml (docs/handover-backend.md, BR-168).
set -euo pipefail
repo=${1:?the console checkout or mirror (a git repository holding the commit)}
commit=${2:?the console commit, as a hash}
root="$(cd "$(dirname "$0")/.." && pwd)"
dest="$root/deploy/helm/insys/files/keycloak-theme"
src=keycloak-theme/insys
max=716800   # 700 KiB of files: about 933 KiB in base64, under a ConfigMap's 1 MiB with its keys and metadata
fail() { echo "sync-keycloak-theme: $*" >&2; exit 1; }

[[ $commit =~ ^[0-9a-f]{7,40}$ ]] || fail "give the commit as a hash, not a branch or tag: \"$commit\" could move"
git -C "$repo" rev-parse --git-dir >/dev/null 2>&1 || fail "$repo isn't a git repository"
full=$(git -C "$repo" rev-parse --verify --quiet "$commit^{commit}") || fail "$repo has no commit $commit (fetch it first)"
listing=$(git -C "$repo" ls-tree -r -l --full-tree "$full" -- "$src/")
[ -n "$listing" ] || fail "$full has no folder $src"

echo "$src at $full:"
bad=() total=0 have_properties=no
while IFS= read -r line; do
  read -r mode type blob size <<<"${line%%$'\t'*}"
  path=${line#*$'\t'}
  rel=${path#"$src/"}
  printf '  %-60s %8s bytes\n' "$rel" "$size"
  [ "$rel" = login/theme.properties ] && have_properties=yes
  if [ "$type" != blob ] || { [ "$mode" != 100644 ] && [ "$mode" != 100755 ]; }; then bad+=("$rel: not a plain file (mode $mode, $type $blob)"); continue; fi
  [[ $rel =~ ^[A-Za-z0-9._/-]+$ ]] && [[ $rel != *__* ]] && [[ /$rel/ != */../* ]] && [[ /$rel/ != */./* ]] \
    || { bad+=("$rel: a ConfigMap key can't hold this name (letters, digits, '.', '-', '_' and '/' only, no '__')"); continue; }
  case "${rel,,}" in
    *.js|*.mjs|*.cjs) bad+=("$rel: a script, and the theme has no scripts of its own") ;;
    *.ftl|*.properties|*.css|*.png|*.jpg|*.jpeg|*.gif|*.svg|*.webp|*.ico) total=$((total + size)) ;;
    *) bad+=("$rel: not a kind the theme holds (.ftl, .properties, .css or an image)") ;;
  esac
done <<<"$listing"
[ "$have_properties" = yes ] || bad+=("login/theme.properties: missing, so this isn't a login theme")
[ "$total" -le "$max" ] || bad+=("$total bytes in all, more than one ConfigMap holds ($max)")
if [ "${#bad[@]}" -gt 0 ]; then
  printf '  refused: %s\n' "${bad[@]}" >&2
  fail "nothing was changed"
fi

# From git's objects only: the archive, each file then checked against its blob, so no attribute or filter changed one.
work=$(mktemp -d)
trap 'rm -rf "$work"' EXIT
git -C "$repo" archive --format=tar "$full" -- "$src" | tar -x -C "$work"
while IFS= read -r line; do
  read -r _ _ blob _ <<<"${line%%$'\t'*}"
  path=${line#*$'\t'}
  [ "$(git hash-object --no-filters "$work/$path")" = "$blob" ] || fail "${path#"$src/"} came out of the archive other than its blob; nothing was changed"
done <<<"$listing"
[ "$(cd "$work/$src" && find . -type f | wc -l)" = "$(wc -l <<<"$listing")" ] || fail "the archive holds other files than the commit's folder; nothing was changed"

rm -rf "$dest/insys"
mkdir -p "$dest"
mv "$work/$src" "$dest/insys"
printf '%s\n' "$full" > "$dest/insys.commit"
echo "Copied $(wc -l <<<"$listing") files ($total bytes) into deploy/helm/insys/files/keycloak-theme/insys/; insys.commit is $full."
echo "In the same commit, set keycloak.loginTheme: insys in deploy/preview/values.yaml (docs/handover-backend.md, BR-168)."
