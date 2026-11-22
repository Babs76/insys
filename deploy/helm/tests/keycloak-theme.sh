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
# The chart's Keycloak login theme (BR-168), rendered both ways: with no files in files/keycloak-theme/insys/ (as the chart is committed until
# the console's theme is synced) nothing extra renders; with this folder's stand-in theme copied into a copy of the chart, the ConfigMap holds
# every file in binaryData (the image byte for byte), the Deployment maps each key back to its path under /opt/keycloak/themes/insys, read
# only, and its checksum annotation is the sum deploy/keycloak/theme-mount.sh computes on the Preview from the ConfigMap. The stand-in is never
# copied into the chart itself. Run by CI's deployment job, with helm from the same image as its other chart checks.
#
#   deploy/helm/tests/keycloak-theme.sh      prints PASS per check, or the first failure, and exits non-zero on it
set -euo pipefail
here="$(cd "$(dirname "$0")" && pwd)"
chart="$here/../insys"
fixture="$here/keycloak-theme/insys"
image=${HELM_IMAGE:-alpine/helm:3.19.0}
work=$(mktemp -d)
trap 'rm -rf "$work"' EXIT
fail() { echo "FAIL  $*" >&2; exit 1; }
pass() { echo "PASS  $*"; }
render() { # the chart folder (under $work), then helm's arguments after the chart
  local dir=$1; shift
  docker run --rm -v "$work:/work:ro" "$image" template t "/work/$dir" --set kms.address=http://bao:8200,objectStore.serviceUrl=http://s3:9000 "$@"
}

[ ! -e "$chart/files/keycloak-theme" ] || [ -f "$chart/files/keycloak-theme/insys/login/theme.properties" ] \
  || fail "the chart's files/keycloak-theme holds something other than a synced theme"
cp -r "$chart" "$work/bare"
rm -rf "$work/bare/files/keycloak-theme"   # the chart as committed before a sync, whatever this checkout holds
cp -r "$chart" "$work/themed"
rm -rf "$work/themed/files/keycloak-theme"
mkdir -p "$work/themed/files/keycloak-theme"
cp -r "$fixture" "$work/themed/files/keycloak-theme/insys"

# 1. No files: nothing extra, and the realm names no login theme.
out=$(render bare --set keycloak.enabled=true)
! grep -v '^ *#' <<<"$out" | grep -q 'keycloak-theme' || fail "with no theme files, the chart rendered something of the theme"
! grep -q '"loginTheme"' <<<"$out" || fail "with no theme files, the realm names a login theme"
pass "no theme files: no ConfigMap, volume, mount or checksum, and no loginTheme in the realm"
if render bare --set keycloak.enabled=true,keycloak.loginTheme=insys >/dev/null 2>&1; then fail "keycloak.loginTheme insys rendered with no theme files"; fi
pass "no theme files: keycloak.loginTheme insys is refused"

# 2. The stand-in theme: the ConfigMap, every file in binaryData, byte for byte.
out=$(render themed --set keycloak.enabled=true,keycloak.loginTheme=insys)
grep -q '^  name: t-keycloak-theme$' <<<"$out" || fail "no ConfigMap t-keycloak-theme"
grep -q '^binaryData:$' <<<"$out" || fail "the theme's ConfigMap has no binaryData"
files=$(cd "$fixture" && find . -type f | sed 's#^\./##' | LC_ALL=C sort)
count=0
expected='{}'
for f in $files; do
  key=${f//\//__}
  b64=$(base64 -w0 "$fixture/$f")
  grep -qxF "  $key: $b64" <<<"$out" || fail "the ConfigMap's $key isn't $f in base64"
  grep -qxF "              - {key: $key, path: $f}" <<<"$out" || fail "the volume doesn't map $key to $f"
  expected=$(jq -c --arg k "$key" --arg v "$b64" '. + {($k): $v}' <<<"$expected")
  count=$((count + 1))
done
[ "$(grep -c '^  [-._a-zA-Z0-9]*: ' <<<"$(sed -n '/^binaryData:$/,/^---/p' <<<"$out")")" = "$count" ] || fail "the ConfigMap holds other keys than the theme's $count files"
[ "$(grep -c -- '- {key: ' <<<"$out")" = "$count" ] || fail "the volume maps other keys than the theme's $count files"
grep -q 'login__resources__img__fixture-mark.png: ' <<<"$out" || fail "the image isn't in the ConfigMap"
pass "the stand-in theme: ConfigMap t-keycloak-theme, all $count files in binaryData (the PNG byte for byte), each mapped back to its path"

# 3. The mount, read only, the volume from the theme's ConfigMap, and the checksum theme-mount.sh computes.
grep -qxF '            - {name: keycloak-theme, mountPath: /opt/keycloak/themes/insys, readOnly: true}' <<<"$out" || fail "no read-only mount at /opt/keycloak/themes/insys"
{ grep -qxF '        - name: keycloak-theme' <<<"$out" && grep -qxF '            name: t-keycloak-theme' <<<"$out"; } || fail "no volume keycloak-theme from the ConfigMap"
sum=$(jq -jcS . <<<"$expected" | sha256sum | cut -d' ' -f1)
grep -qxF "        checksum/keycloak-theme: $sum" <<<"$out" || fail "the checksum annotation isn't $sum, the sum of the ConfigMap's binaryData as theme-mount.sh computes it"
pass "the mount at /opt/keycloak/themes/insys (read only), and the checksum annotation $sum"
grep -q '"loginTheme": "insys"' <<<"$out" || fail "keycloak.loginTheme insys isn't in the realm"
pass "keycloak.loginTheme insys is in the realm's import"
out=$(render themed --set keycloak.enabled=true --show-only templates/keycloak-theme.yaml)
{ [ "$(grep -c '^kind: ' <<<"$out")" = 1 ] && grep -q '^kind: ConfigMap$' <<<"$out"; } || fail "--show-only templates/keycloak-theme.yaml isn't the ConfigMap alone"
pass "templates/keycloak-theme.yaml renders the ConfigMap alone (the Preview's keycloak-theme action applies it)"

# 4. A change of theme changes the checksum; Keycloak off renders none of it; a file a key can't hold is refused.
printf '\n/* changed */\n' >> "$work/themed/files/keycloak-theme/insys/login/resources/css/insys.css"
! render themed --set keycloak.enabled=true | grep -qxF "        checksum/keycloak-theme: $sum" || fail "a changed file kept the checksum"
pass "a changed file changes the checksum, so Keycloak restarts"
! render themed | grep -v '^ *#' | grep -q 'keycloak-theme' || fail "with Keycloak off, the theme rendered"
pass "keycloak.enabled false: nothing of the theme"
touch "$work/themed/files/keycloak-theme/insys/login/bad name.css"
if render themed --set keycloak.enabled=true >/dev/null 2>&1; then fail "a file named with a space rendered"; fi
pass "a file whose name a ConfigMap key can't hold is refused"
