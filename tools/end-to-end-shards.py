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
"""Splits the dated end-to-end process tests into balanced shards by test method (OQ-129, Ade, 19 November 2026).

The dated jobs ran each clock as two jobs by class, and took 20 to 42 minutes against a 45-minute limit: the stall summaries
showed the runner starved (threads busy, nothing queued, the database idle), not a test stuck. A class is too big for one
job (General has 32 tests in sequence), so the unit here is the method: each shard runs a balanced share of every dated
class's methods.

Usage (each reads the compiled methods, the test assembly's "-list methods" output, on stdin):
  end-to-end-shards.py SHARD COUNT    prints the --filter-method arguments of shard SHARD (1-based) of COUNT.
  end-to-end-shards.py --check COUNT  lists each shard and fails unless every class is dated or named as not dated, and
                                      every dated method is in exactly one shard.
Without stdin:
  end-to-end-shards.py --times REPORT           prints each test's seconds from an xUnit XML report, as "e2e-time" lines,
                                                into the job's log, where the API can read them (no artifact needed).
  end-to-end-shards.py --measure LOG...         rewrites the timings file from job logs holding those lines: each method's
                                                longest time across them.

A method weighs its measured seconds from the timings file (tests/Insys.EndToEndTests/method-timings.json); one never
measured is estimated. Methods are placed heaviest first on the lightest shard, so a given listing and timings file always
give the same split, in every job of a run.
"""
import json
import pathlib
import re
import sys
import xml.etree.ElementTree as ElementTree

ROOT = pathlib.Path(__file__).resolve().parent.parent / "tests" / "Insys.EndToEndTests"
TIMINGS = ROOT / "method-timings.json"
PREFIX = "Insys.EndToEndTests."
# The classes whose flows turn on dates, run on each fixed clock (WindowClock.Dates).
DATED = {"GeneralProcessTests", "LifeProcessTests", "HealthProcessTests", "EnterpriseProcessTests"}
# Run once, in sweeps-and-end-to-end, not on the clocks.
NOT_DATED = {"ProcessTests", "ProductPublicationTests"}
ESTIMATE = 60
TIME_LINE = re.compile(r"e2e-time (\S+) (\d+(?:\.\d+)?)")


def listed_methods(lines):
    return sorted({line.strip() for line in lines if line.strip().startswith(PREFIX) and line.strip().count(".") == 3})


def class_of(method):
    return method.split(".")[2]


def weights(methods):
    times = json.loads(TIMINGS.read_text(encoding="utf-8"))["methods"] if TIMINGS.exists() else {}
    return {m: times.get(m.removeprefix(PREFIX), ESTIMATE) for m in methods}


def shards(methods, count):
    buckets = [[0.0, []] for _ in range(count)]
    for method, weight in sorted(weights(methods).items(), key=lambda m: (-m[1], m[0])):
        lightest = min(buckets, key=lambda b: b[0])
        lightest[0] += weight
        lightest[1].append(method)
    return buckets


def dated(methods):
    return [m for m in methods if class_of(m) in DATED]


def times(report):
    seconds = {}
    for test in ElementTree.parse(report).getroot().iter("test"):
        kind, method = test.get("type"), test.get("method")
        if kind and method:
            name = f"{kind.removeprefix(PREFIX)}.{method}"
            seconds[name] = seconds.get(name, 0.0) + float(test.get("time") or 0)
    for name, value in sorted(seconds.items(), key=lambda t: -t[1]):
        print(f"e2e-time {name} {value:.1f}")
    print(f"e2e-total {sum(seconds.values()):.1f} seconds in {len(seconds)} methods")
    return 0


def measure(logs):
    longest = {}
    for log in logs:
        for match in TIME_LINE.finditer(pathlib.Path(log).read_text(encoding="utf-8", errors="replace")):
            name, value = match.group(1), round(float(match.group(2)))
            longest[name] = max(longest.get(name, 0), value)
    TIMINGS.write_text(json.dumps({"source": "the longest of each method across the job logs given", "methods": dict(sorted(longest.items()))},
                                  indent=1) + "\n", encoding="utf-8")
    print(f"{len(longest)} methods measured")
    return 0


def main(args):
    if args[:1] == ["--times"]:
        return times(args[1])
    if args[:1] == ["--measure"]:
        return measure(args[1:])

    methods = listed_methods(sys.stdin)
    if args[:1] == ["--check"]:
        count = int(args[1])
        classes = {class_of(m) for m in methods}
        unknown = sorted(classes - DATED - NOT_DATED)
        missing = sorted((DATED | NOT_DATED) - classes)
        if unknown or missing:
            print("end-to-end classes not named as dated or not dated: " + ", ".join(unknown) if unknown else "",
                  "named but not compiled: " + ", ".join(missing) if missing else "")
            return 1
        buckets = shards(dated(methods), count)
        assigned = [m for _, names in buckets for m in names]
        for i, (weight, names) in enumerate(buckets, 1):
            print(f"shard {i}: {len(names)} methods, weight {weight:.0f} seconds, " +
                  ", ".join(f"{c} {sum(1 for n in names if class_of(n) == c)}" for c in sorted(DATED)))
        if sorted(assigned) != sorted(dated(methods)) or len(assigned) != len(set(assigned)):
            print("a dated method is in no shard or in two")
            return 1
        return 0

    shard, count = int(args[0]), int(args[1])
    names = sorted(shards(dated(methods), count)[shard - 1][1])
    if not names:
        print("this shard has no method", file=sys.stderr)
        return 1
    print(" ".join(f'--filter-method "{m}"' for m in names))
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
