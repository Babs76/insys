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
# Usage: tools/locked.sh --wait "<run id>" tools/full-sweep.sh   (build first; set INSYS_TEST_DEFAULT_TIME_ZONE for a zone sweep)
# Every integration test on one machine, split as CI splits them: a process per shard, and one for the endpoint sweeps. One process
# for all of them isn't reliable: a disposed host stays in memory (OQ-162), and after 100 hosts or so requests and events time out.
cd "${0:A:h}/.." || exit 1
out=${INSYS_SWEEP_OUT:-/tmp/insys-full-sweep}; rm -rf "$out"; mkdir -p "$out"
total=0; failed=0; bad=0
run() {
  name=$1; shift
  dotnet test --project tests/Insys.IntegrationTests --no-build -- "$@" > "$out/part-$name.txt" 2>&1 || bad=1
  sed 's/\x1b\[[0-9;]*m//g' "$out/part-$name.txt" | grep -E '^failed ' | cut -c1-200
  summary=$(sed 's/\x1b\[[0-9;]*m//g' "$out/part-$name.txt" | grep -E '^  (total|failed):' | awk '{print $2}' | tr '\n' ' ')
  echo "$name: total/failed $summary"
  set -- ${=summary}
  total=$((total + ${1:-0})); failed=$((failed + ${2:-0}))
}
for shard in 1 2 3 4; do
  run "shard-$shard" ${=$(python3 tools/integration-shards.py $shard 4)}
done
run "sweeps" --filter-class "*EndpointSecuritySweepTests"
echo "all: $total tests, $failed failed"
[ $bad = 0 ] && [ $failed = 0 ] && [ $total -gt 0 ]
