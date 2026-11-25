#!/usr/bin/env bash
# ============================================================================
# OPENFACTOR TECHNOLOGY GROUP
# CONFIDENTIAL AND PROPRIETARY SOFTWARE
# ============================================================================
# Copyright (c) 2026 OpenFactor Technology Group.
# All rights reserved.
# PRODUCT       : INSYS
# COMPONENT     : CI
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
# The Preview's console-image action (deploy/preview/preview.sh: console_image and check (vi), post_console; Ade, 25 November 2026), against a
# stubbed server: it changes only deploy/insys-console's image, to the chart's pinned digest; it stops before any change when the pinned
# image doesn't resolve; and when check (vi) fails it sets the previous image back. No network, no server.
set -euo pipefail
repo=$(cd "$(dirname "$0")/../../.." && pwd)
work=$(mktemp -d)
trap 'rm -rf "$work"' EXIT

sed -n '/^console_image()/,/^}/p;/^post_console()/,/^}/p;/^post_why()/p' "$repo/deploy/preview/preview.sh" > "$work/functions.sh"
for f in console_image post_console post_why; do grep -q "^$f()" "$work/functions.sh" || { echo "$f not found in preview.sh"; exit 1; }; done
want=$(sed -n 's/^  image: \(ghcr.io\/[^ ]*@sha256:[0-9a-f]*\).*/\1/p' "$repo/deploy/helm/insys/values.yaml")
[ -n "$want" ] || { echo "the chart pins no console image"; exit 1; }
old=ghcr.io/babs76/insys-console-web@sha256:$(printf '0%.0s' $(seq 1 64))

# One scenario, in a subshell: $1 its name; the stubs read the others from the environment.
#   PULL     ok or bad: whether the pinned image resolves
#   RUNS     what the pods' imageID shows once the image is set: set (the deploy's image) or $old
#   MEMBER   the member portal's HTTP code
# shellcheck disable=SC2034,SC2329   # the stubs and here are used by the functions sourced from preview.sh
run() (
  export RUNNER_TEMP=$work/$1 RUNNER_IP=192.0.2.1 GITHUB_STEP_SUMMARY=$work/$1/summary PREVIEW_DOMAIN=insys.example PREVIEW_ACME=production
  mkdir -p "$RUNNER_TEMP"; : > "$RUNNER_TEMP/log"; printf '%s' "$old" > "$RUNNER_TEMP/image"
  here=$repo/deploy/preview
  fail() { echo "::error::$*" >&2; exit 1; }
  summary() { printf '%s\n\n' "$*" >> "$GITHUB_STEP_SUMMARY"; }
  mask() { :; }
  ssh_key() { echo "ssh_key" >> "$RUNNER_TEMP/log"; }
  open_ssh() { echo "open_ssh $1" >> "$RUNNER_TEMP/log"; }
  wait_for_ssh() { :; }
  check_pull() { if [ "$PULL" = ok ]; then echo "reads the pinned manifest"; else echo "the pinned manifest answers HTTP 404"; return 1; fi; }
  on_server() {
    local cmd="$*" image; image=$(cat "$RUNNER_TEMP/image")
    echo "server: $cmd" >> "$RUNNER_TEMP/log"
    case "$cmd" in
      "kubectl get deploy insys-console -o jsonpath='{.spec.template.spec.containers[0].image}'") printf '%s' "$image" ;;
      "kubectl set image deploy/insys-console console='"*) image=${cmd#*console=\'}; printf '%s' "${image%%\'*}" > "$RUNNER_TEMP/image" ;;
      "kubectl rollout status deploy/insys-console --timeout=180s") ;;
      "kubectl get pods -l app.kubernetes.io/name=console"*)
        [ "$RUNS" = set ] && echo "insys-console-a true ghcr.io/x@${image#*@}" || echo "insys-console-a true ghcr.io/x@${RUNS#*@}" ;;
      *) echo "unexpected server command: $cmd" >> "$RUNNER_TEMP/unexpected"; return 1 ;;
    esac
  }
  curl() { # only post_console's page reads: -s -o <file> -w <format> --max-time 20 <url>
    local file="" url="${*: -1}" code=200
    while [ $# -gt 0 ]; do [ "$1" = -o ] && file=$2; shift; done
    case "$url" in */member/) code=$MEMBER ;; esac
    case "$url" in
      */config.json) echo '{}' > "$file"; printf '%s application/json' "$code" ;;
      *) echo '<html></html>' > "$file"; printf '%s text/html' "$code" ;;
    esac
  }
  # shellcheck disable=SC1091
  . "$work/functions.sh"
  console_image
)

pass=0
expect() { # name, wanted outcome (ok or fail), wanted final image, then the scenario's settings
  local name=$1 outcome=$2 image=$3 got; shift 3
  if env "$@" bash -c "$(declare -f run); $(declare -p work repo old); run $name" > "$work/$name.out" 2>&1; then got=ok; else got=fail; fi
  [ "$got" = "$outcome" ] || { echo "FAIL $name: $got, not $outcome"; cat "$work/$name.out"; exit 1; }
  [ "$(cat "$work/$name/image")" = "$image" ] || { echo "FAIL $name: the console's image is $(cat "$work/$name/image"), not $image"; exit 1; }
  [ ! -s "$work/$name/unexpected" ] || { echo "FAIL $name: a command other than the console's"; cat "$work/$name/unexpected"; exit 1; }
  pass=$((pass + 1)); echo "PASS $name"
}

expect sets-the-pinned-digest ok "$want" PULL=ok RUNS=set MEMBER=200
grep -q "^server: kubectl set image deploy/insys-console console='$want' >/dev/null$" "$work/sets-the-pinned-digest/log" || { echo "FAIL: no set image to the pinned digest"; exit 1; }
[ "$(grep -c '^server: kubectl set image' "$work/sets-the-pinned-digest/log")" = 1 ] || { echo "FAIL: the image was set more than once"; exit 1; }
grep -q "The console's image is ${want#*@}" "$work/sets-the-pinned-digest/summary" || { echo "FAIL: the summary doesn't name the digest"; exit 1; }

expect stops-when-the-pin-does-not-resolve fail "$old" PULL=bad RUNS=set MEMBER=200
[ ! -s "$work/stops-when-the-pin-does-not-resolve/log" ] || { echo "FAIL: it reached the server before the pull check"; cat "$work/stops-when-the-pin-does-not-resolve/log"; exit 1; }

expect sets-back-when-the-member-portal-fails fail "$old" PULL=ok RUNS=set MEMBER=502
grep -q "previous image .* is set back" "$work/sets-back-when-the-member-portal-fails/summary" || { echo "FAIL: no set-back in the summary"; exit 1; }

expect sets-back-when-a-pod-runs-another-digest fail "$old" PULL=ok RUNS="$old" MEMBER=200

echo "$pass scenarios passed"
