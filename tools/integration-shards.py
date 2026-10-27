#!/usr/bin/env python3
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
"""Splits the integration test classes into balanced shards for CI (Wave 1 batch 4; three shards from Wave 2 batch 4).

Usage: integration-shards.py SHARD COUNT   prints the --filter-class arguments of shard SHARD (1-based) of COUNT.
       integration-shards.py --check COUNT  lists each shard and fails unless every class is in exactly one.
       integration-shards.py --check-listed COUNT  reads the compiled classes (the test assembly's "-list classes"
                                                    output) on stdin and fails unless each one is in a shard or excluded.
       integration-shards.py --measure REPORT...  rewrites the timings file from xUnit XML reports (--report-xunit-xml).

Every test class in tests/Insys.IntegrationTests is assigned, so a new class always runs. The endpoint sweeps run in
their own job and are left out. A class weighs its measured seconds from the timings file (TIMINGS, or the file named by
INSYS_SHARD_TIMINGS): the time it held a parallel slot in a full run, from its first test's start to its last test's
finish. Every CI run measures its shards and publishes the merged file as the "integration-timings" artifact; the next
run balances from the latest one on main, so no weight is edited by hand. Only a class never measured is estimated from
its tests. Classes are placed heaviest first on the lightest shard, so the split is stable for a given timings file.
"""
import datetime
import json
import os
import pathlib
import re
import sys
import xml.etree.ElementTree as ElementTree

ROOT = pathlib.Path(__file__).resolve().parent.parent / "tests" / "Insys.IntegrationTests"
TIMINGS = ROOT / "class-timings.json"
EXCLUDED = {"EndpointSecuritySweepTests"}
# A class never measured: start-up plus a share per test, until a run measures it.
ESTIMATE_BASE = 60
ESTIMATE_PER_TEST = 50


def timings_path():
    return pathlib.Path(os.environ.get("INSYS_SHARD_TIMINGS") or TIMINGS)


def measured():
    path = timings_path()
    return json.loads(path.read_text(encoding="utf-8"))["classes"] if path.exists() else {}


def classes():
    times = measured()
    found = {}
    for path in sorted(ROOT.glob("*.cs")):
        text = path.read_text(encoding="utf-8")
        for name in re.findall(r"\bclass (\w+Tests)\b", text):
            if name not in EXCLUDED:
                tests = len(re.findall(r"^\s*\[(?:Fact|Theory|InlineData)", text, re.M))
                found[name] = times.get(name, ESTIMATE_BASE + ESTIMATE_PER_TEST * tests)
    return found


def measure(reports):
    """Each class's slot time across the reports: first test start to last test finish, whole seconds (at least 1)."""
    spans = {}
    for report in reports:
        for test in ElementTree.parse(report).getroot().iter("test"):
            name = (test.get("type") or "").rsplit(".", 1)[-1]
            start, finish = test.get("start-rtf"), test.get("finish-rtf")
            if not name or not start or not finish:
                continue
            start, finish = datetime.datetime.fromisoformat(start), datetime.datetime.fromisoformat(finish)
            first, last = spans.get(name, (start, finish))
            spans[name] = (min(first, start), max(last, finish))
    return {name: max(1, round((last - first).total_seconds())) for name, (first, last) in spans.items()}


def shards(count):
    buckets = [[0, []] for _ in range(count)]
    for name, weight in sorted(classes().items(), key=lambda c: (-c[1], c[0])):
        lightest = min(buckets, key=lambda b: b[0])
        lightest[0] += weight
        lightest[1].append(name)
    return buckets


def main(args):
    if args[:1] == ["--measure"]:
        # Classes the reports measured take their new time; one they missed (a failed shard) keeps its last; one no longer
        # in the source is dropped.
        current = set(classes())
        times = {name: t for name, t in measured().items() if name in current}
        times.update({name: t for name, t in measure(args[1:]).items() if name in current})
        timings_path().write_text(json.dumps({"source": os.environ.get("INSYS_SHARD_SOURCE", "local run"), "classes": dict(sorted(times.items()))},
                                             indent=1) + "\n", encoding="utf-8")
        unmeasured = sorted(current - set(times))
        print(f"{len(times)} classes measured" + (f"; estimated: {', '.join(unmeasured)}" if unmeasured else ""))
        return 0

    if args[:1] == ["--check"]:
        count = int(args[1])
        assigned = [name for _, names in shards(count) for name in names]
        print(f"timings: {timings_path()}")
        for i, (weight, names) in enumerate(shards(count), 1):
            print(f"shard {i}: weight {weight} (class seconds), {len(names)} classes")
        if sorted(assigned) != sorted(classes()) or len(assigned) != len(set(assigned)):
            print("a class is in no shard or in two")
            return 1
        return 0

    if args[:1] == ["--check-listed"]:
        # The source scan above finds classes by name; this compares it with what the build actually contains, so a
        # test class the scan misses (another name, another folder) fails CI instead of silently never running.
        assigned = {name for _, names in shards(int(args[1])) for name in names} | EXCLUDED
        listed = {line.strip().rsplit(".", 1)[-1] for line in sys.stdin if line.strip().startswith("Insys.IntegrationTests.")}
        missing = sorted(listed - assigned)
        print(f"{len(listed)} compiled classes, {len(listed) - len(missing)} assigned")
        if missing:
            print("not in any shard: " + ", ".join(missing))
            return 1
        return 0

    shard, count = int(args[0]), int(args[1])
    print(" ".join(f'--filter-class "*.{name}"' for name in sorted(shards(count)[shard - 1][1])))
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
