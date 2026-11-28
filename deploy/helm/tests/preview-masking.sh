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
# No secret value in any Preview action's output (Ade, 28 November 2026, after verify run 46111295910 printed Keycloak's admin password).
# GitHub hides a value only after "::add-mask::<value>" reaches the step's output at the start of a line; inside a captured result it lands
# mid-line and prints the value instead. Three checks:
#   1. preview.sh's own mask reaches the step's output on a line of its own from inside captures, pipes, subshells and after a partial line,
#      and leaves nothing in the captured result;
#   2. every secret preview.sh reads back to the runner (Keycloak's admin password and token, OpenBao's keys and token, the registry bearer,
#      the seed's account passwords, a generated password) is masked in the same function before anything else, and nothing bypasses mask;
#   3. the stubbed action tests that handle a secret (verify, upgrade-check) run with this mask and assert their whole output with
#      deploy/helm/tests/secret-output.sh: a line holding a secret is only ever a mask command.
# shellcheck disable=SC2016 # patterns over preview.sh's text, as written
set -euo pipefail
repo=$(cd "$(dirname "$0")/../../.." && pwd)
p="$repo/deploy/preview/preview.sh"
work=$(mktemp -d)
trap 'rm -rf "$work"' EXIT
# shellcheck source=deploy/helm/tests/secret-output.sh disable=SC1091 # sourced at run time
. "$repo/deploy/helm/tests/secret-output.sh"
failures=0
bad() { echo "FAIL: $*"; failures=$((failures + 1)); }

# 1. The mask itself.
grep -qx 'exec 9>&1' "$p" || bad "preview.sh doesn't open fd 9 on the step's output"
[ "$(grep -n '^exec 9>&1$' "$p" | cut -d: -f1 || true)" -lt "$(grep -n '^[a-z_]*() *{' "$p" | sed -n 2p | cut -d: -f1)" ] 2>/dev/null || bad "fd 9 is opened after functions that may mask"
sed -n '/^mask() *{/p' "$p" > "$work/mask.sh"
[ -s "$work/mask.sh" ] || bad "no one-line mask() in preview.sh"
secret=s3cr3t-sentinel-a1b2c3 secret2=pipe-sentinel-d4e5f6 secret3=subshell-sentinel-g7h8i9
(
  exec 9>&1
  # shellcheck disable=SC1091 # extracted at run time
  . "$work/mask.sh"
  captured=$(printf 'partial line, no newline: '; mask "$secret"; echo after)
  piped=$( { mask "$secret2"; echo piped; } | cat )
  ( mask "$secret3" ) > /dev/null
  mask ""
  printf 'PASS-looking line: %s|%s\n' "$captured" "$piped"
) > "$work/unit.out" 2>&1
grep -q "$secret" <(grep -v '^::add-mask::' "$work/unit.out") && bad "a captured mask left the value in its result: $(grep -v '^::add-mask::' "$work/unit.out" | grep "$secret")"
for v in "$secret" "$secret2" "$secret3"; do grep -qx "::add-mask::$v" "$work/unit.out" || bad "no mask line of its own for a value masked inside a capture, pipe or subshell ($v)"; done
grep -qx '::add-mask::' "$work/unit.out" && bad "an empty value was masked (that would mask nothing, or everything)"
only_masks "$work/unit.out" "$secret" "$secret2" "$secret3" || bad "the unit run printed a secret outside a mask line"

# Nothing writes a mask command but mask, and no mask call redirects it.
grep -n 'add-mask' "$p" | grep -v '^[0-9]*:mask() *{' | grep -v '^[0-9]*:#' | grep -v 'add-mask:: on\|"::add-mask::<value>"' > "$work/other" || true
[ ! -s "$work/other" ] || bad "a mask command written other than by mask(): $(cat "$work/other")"
grep -nE '(^|[;&|{( ])mask [^#]*>' "$p" | grep -v '^[0-9]*:mask() *{' > "$work/redirected" || true
[ ! -s "$work/redirected" ] || bad "a mask call redirected (mask writes to the step's output itself): $(cat "$work/redirected")"

# The browser check (theme_check, deploy/keycloak/theme-check.cjs) masks with its own console.log, on its own lines of the step's output:
# that holds only while its output isn't captured.
grep -nE '\$\( *theme_check|=\$\(.*theme-check\.cjs' "$p" && bad "theme_check's output is captured, so its masks would land in a result"
grep -q 'function mask(value) { if (process.env.GITHUB_ACTIONS === .true. && value) console.log(`::add-mask::${value}`); }' "$repo/deploy/keycloak/theme-check.cjs" \
  || bad "theme-check.cjs's mask isn't a console.log line of its own"

# 2. Every secret read back to the runner is masked in the same function, within the next three lines.
masked_after() { # line number, variable
  sed -n "$(($1 + 1)),$(($1 + 3))p" "$p" | grep -qE "(^|[;&{( ])mask [^#]*\"\\\$$2\"|(^|[;&{( ])mask [^#]*<<<\"\\\$$2\"|read -r $2; do mask \"\\\$$2\""
}
sources='keycloakAdminPassword\}.*base64 -d"\)|\.root_token|\.access_token|openssl rand|openbao\.sh init|/token\?scope=|\.accounts\[\]\.password'
grep -nE "$sources" "$p" | grep -v '^[0-9]*: *#' > "$work/sources"
[ "$(wc -l < "$work/sources")" -ge 8 ] || bad "found only $(wc -l < "$work/sources") secret reads; the patterns no longer match preview.sh"
while IFS= read -r hit; do
  n=${hit%%:*}; text=${hit#*:}
  var=$(sed -nE 's/^ *(local +)?([a-z_]+)=\$\(.*/\2/p' <<<"$text")
  if [ -z "$var" ]; then   # an assignment that starts on the line before (bearer=$(printf ... \n | curl ... token ...))
    var=$(sed -n "$((n - 1))p" "$p" | sed -nE 's/^ *(local +)?([a-z_]+)=\$\(.*\\$/\2/p')
  fi
  # Into the encrypted file only (encrypt: jq straight into age), never to the output.
  sed -n "$n,$((n + 2))p" "$p" | grep -q '| age -r "\$PREVIEW_AGE_RECIPIENT"' && continue
  case "$text" in
    *"read -r password"*) var=password; n=$((n - 1)) ;;
    *'.accounts[].password'*) var=password; n=$((n - 1)) ;;
  esac
  if [ -z "$var" ]; then
    # Not read into a variable: it must stay on the server or go straight into a mask or a pipe to the server.
    grep -qE 'mask "\$\(|\| *on_server|on_server "[^"]*\|' <<<"$text" || bad "line $n reads a secret without a variable to mask: $text"
    continue
  fi
  masked_after "$n" "$var" || masked_after "$((n - 1))" "$var" || grep -qE "mask [^#]*<<<\"\\\$$var\"" <<<"$(sed -n "$n,$((n + 3))p" "$p")" \
    || bad "line $n reads a secret into \$$var with no mask of it in the next three lines"
done < "$work/sources"

# 3. The stubbed action tests run with preview.sh's own mask and check their whole output.
for t in preview-verify.sh preview-upgrade-check.sh; do
  grep -q 'mask() *{ *: *; *}' "$repo/deploy/helm/tests/$t" && bad "$t stubs mask away, so it can't see a value printed"
  grep -q 'only_masks ' "$repo/deploy/helm/tests/$t" || bad "$t doesn't check its output for secrets"
done

[ "$failures" -eq 0 ] || { echo "$failures failure(s)"; exit 1; }
echo "masking: every check as expected"
