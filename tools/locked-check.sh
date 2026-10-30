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
# Checks tools/locked.sh and lock_state.py against the agreed lock format, on a private lock file. Needs no container.
here=${0:A:h}
dir=$(mktemp -d)
failures=0
check() { # name expected actual
  if [ "$2" = "$3" ]; then echo "ok   $1"; else echo "FAIL $1: expected [$2], got [$3]"; failures=$((failures + 1)); fi
}
old=2026-01-01T00:00:00.000Z
recent=$(python3 -c 'from datetime import datetime as d, timezone as z; print(d.now(z.utc).strftime("%Y-%m-%dT%H:%M:%SZ"))')
lockfile() { printf 'holder=%s\npid=%s\ntaken_at=%s\nheartbeat_at=%s\n' "$2" "$3" "$old" "$4" > "$1"; }
state() { python3 "$here/lock_state.py" "$1" | cut -d' ' -f1; }

check "no file is free" free "$(state $dir/none)"
lockfile $dir/a "frontend pin-1" 99999 $old;   check "old heartbeat, dead pid: stale" stale "$(state $dir/a)"
lockfile $dir/b "backend br-1" $$ $old;        check "old heartbeat, live pid: held" held "$(state $dir/b)"
lockfile $dir/c "frontend pin-1" 99999 $recent; check "recent heartbeat, dead pid: held" held "$(state $dir/c)"

# A file that can't be parsed is never stale, however old.
printf 'front end, X, %s\n' $old > $dir/d;           check "the old one-line form: unreadable" unreadable "$(state $dir/d)"
lockfile $dir/e "front end, X" 99999 $old;           check "a holder not backend or frontend: unreadable" unreadable "$(state $dir/e)"
lockfile $dir/f "backend br-1" abc $old;             check "a pid not a number: unreadable" unreadable "$(state $dir/f)"
lockfile $dir/g "backend br-1" 99999 yesterday;      check "a heartbeat not a time: unreadable" unreadable "$(state $dir/g)"
printf 'holder=backend x\npid=99999\nheartbeat_at=%s\n' $old > $dir/h; check "a key missing: unreadable" unreadable "$(state $dir/h)"
: > $dir/i;                                          check "an empty file: unreadable" unreadable "$(state $dir/i)"
touch -t 202601010000 $dir/d
INSYS_HEAVY_RUN_LOCK=$dir/d "$here/locked.sh" "t" true > /dev/null; check "an unreadable lock is not taken" 75 $?
check "and is left in place" "front end, X, $old" "$(cat $dir/d)"

export INSYS_LOCK_POLL_SECONDS=1 INSYS_LOCK_HEARTBEAT_SECONDS=1
INSYS_HEAVY_RUN_LOCK=$dir/j "$here/locked.sh" "br-2" cat $dir/j > $dir/j.out; check "the command's result is returned" 0 $?
check "the holder line" "holder=backend br-2" "$(sed -n 1p $dir/j.out)"
check "the pid line" 1 "$(sed -n 2p $dir/j.out | grep -cE '^pid=[0-9]+$')"
check "the taken_at line" 1 "$(sed -n 3p $dir/j.out | grep -cE '^taken_at=[0-9-]+T[0-9:.]+Z$')"
check "the heartbeat_at line" 1 "$(sed -n 4p $dir/j.out | grep -cE '^heartbeat_at=[0-9-]+T[0-9:.]+Z$')"
check "four lines" 4 "$(wc -l < $dir/j.out | tr -d ' ')"
check "released afterwards" free "$(state $dir/j)"
INSYS_HEAVY_RUN_LOCK=$dir/k "$here/locked.sh" "t" false; check "a failing command's result is returned" 1 $?

INSYS_HEAVY_RUN_LOCK=$dir/l "$here/locked.sh" "t" sleep 4 &
p=$!; sleep 1; first=$(grep heartbeat_at $dir/l); sleep 2; check "the heartbeat moves" 1 "$([ "$first" != "$(grep heartbeat_at $dir/l)" ] && echo 1 || echo 0)"; wait $p

INSYS_HEAVY_RUN_LOCK=$dir/m "$here/locked.sh" "t" sleep 30 &
p=$!; sleep 1; kill $p; sleep 1; check "a stopped run releases it" free "$(state $dir/m)"

lockfile $dir/n "backend br-1" 99999 $old
INSYS_HEAVY_RUN_LOCK=$dir/n "$here/locked.sh" "t" true > /dev/null; check "a stale lock is replaced" 0 $?

lockfile $dir/o "backend br-1" $$ $recent
( sleep 2; mv $dir/o $dir/o.gone ) &
check "--wait waits, then takes it" "got it" "$(INSYS_HEAVY_RUN_LOCK=$dir/o "$here/locked.sh" --wait "t" echo got it | tail -1)"

[ $failures = 0 ] && echo "All passed." || echo "$failures failed."
exit $failures
