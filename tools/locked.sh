#!/bin/zsh
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
# Usage: tools/locked.sh [--wait] "<run id or branch>" <command...>
# Runs a container test run under the machine lock shared with the front end. The format and the stale rule are in lock_state.py.
# The holder refreshes heartbeat_at every 2 minutes. Without --wait, a held or unreadable lock exits 75. With it, this keeps waiting,
# and an unreadable lock is reported and never replaced: a person looks at it.
lock=${INSYS_HEAVY_RUN_LOCK:-/tmp/insys-heavy-run.lock}
beat=${INSYS_LOCK_HEARTBEAT_SECONDS:-120}
poll=${INSYS_LOCK_POLL_SECONDS:-30}
here=${0:A:h}
wait=0; if [ "$1" = "--wait" ]; then wait=1; shift; fi
holder="backend $1"; shift
now() { python3 -c 'from datetime import datetime as d, timezone as z; print(d.now(z.utc).strftime("%Y-%m-%dT%H:%M:%S.%f")[:-3]+"Z")'; }
taken=$(now)
write() { printf 'holder=%s\npid=%s\ntaken_at=%s\nheartbeat_at=%s\n' "$holder" $$ "$taken" "$(now)" > "$1"; }
mine() { grep -qx "pid=$$" "$lock" 2>/dev/null; }
tmp="$lock.$$"
said=""
while :; do
  state=$(python3 "$here/lock_state.py" "$lock")
  case "$state" in
    stale*) # One waiter at a time: the look is repeated under a guard, itself given up after a minute.
      guard="$lock.takeover"
      [ -f "$guard" ] && [ $(( $(date +%s) - $(stat -f %m "$guard") )) -gt 60 ] && rm -f "$guard"
      if ( set -C; echo $$ > "$guard" ) 2>/dev/null; then
        case "$(python3 "$here/lock_state.py" "$lock")" in
          stale*) echo "The machine lock was left behind (${state#stale }); replacing it."; rm -f "$lock" ;;
        esac
        rm -f "$guard"
      fi ;;
    held*|unreadable*)
      if [ $wait = 0 ]; then echo "LOCKED: $state"; exit 75; fi
      if [ "$said" != "$state" ]; then
        case "$state" in
          unreadable*) echo "The machine lock can't be read, so it is not replaced; waiting. A person should look at $lock: ${state#unreadable }" ;;
          *) echo "Waiting for the machine lock: ${state#held }" ;;
        esac
        said=$state
      fi
      sleep $poll; continue ;;
  esac
  write "$tmp"
  ln "$tmp" "$lock" 2>/dev/null && { rm -f "$tmp"; break; }   # atomic: fails if someone took it first
  rm -f "$tmp"
done
( while kill -0 $$ 2>/dev/null; do
    sleep $beat
    kill -0 $$ 2>/dev/null && mine && write "$tmp" && mv "$tmp" "$lock"
  done ) &
hb=$!
trap 'kill $hb 2>/dev/null; rm -f "$tmp"; mine && rm -f "$lock"' EXIT
"$@" &
run=$!
trap 'kill $run 2>/dev/null; exit 130' INT TERM HUP
wait $run
